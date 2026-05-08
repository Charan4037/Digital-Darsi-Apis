using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Customer;
using BagistoApi.Models.Sales;

namespace BagistoApi.Services;

/// <summary>
/// FCM push-notification fan-out for the customer portal.
///
/// Talks to <c>FirebaseMessaging.DefaultInstance</c>, which is initialized
/// in <see cref="Program"/> when a service-account credentials file is
/// configured. If Firebase isn't configured on this server every send call
/// short-circuits to a no-op + logged warning — that way local dev runs
/// without surprise crashes when the credentials file is missing.
///
/// Token hygiene: when FCM responds with <c>Unregistered</c> or
/// <c>InvalidArgument</c> for a token we delete the row, so the table
/// doesn't drift into stale-token bloat over time.
/// </summary>
public class NotificationService
{
    private readonly BagistoDbContext _db;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(BagistoDbContext db, ILogger<NotificationService> log)
    {
        _db = db;
        _log = log;
    }

    private static FirebaseMessaging? Messaging =>
        FirebaseApp.DefaultInstance == null ? null : FirebaseMessaging.DefaultInstance;

    // ─── Per-customer (transactional) ───────────────────────────────────

    /// <summary>
    /// Send a notification to every device registered for one customer.
    /// Returns the number of successful sends; tokens FCM reports as
    /// invalid are pruned from the DB before this returns.
    /// </summary>
    public async Task<int> SendToCustomerAsync(
        int customerId,
        string title,
        string body,
        IDictionary<string, string>? data = null,
        string? imageUrl = null)
    {
        var fcm = Messaging;
        if (fcm == null)
        {
            _log.LogWarning("[Notify] Skipped customer {CustomerId} — Firebase not configured.", customerId);
            return 0;
        }

        var rows = await _db.CustomerDeviceTokens
            .Where(t => t.CustomerId == customerId)
            .ToListAsync();
        if (rows.Count == 0) return 0;

        return await SendToTokensAsync(fcm, rows, title, body, data, imageUrl);
    }

    /// <summary>
    /// Send to an arbitrary set of token rows. Used by both per-customer
    /// and admin-broadcast paths so the prune-on-failure logic only lives
    /// in one place.
    /// </summary>
    private async Task<int> SendToTokensAsync(
        FirebaseMessaging fcm,
        IReadOnlyList<CustomerDeviceToken> rows,
        string title,
        string body,
        IDictionary<string, string>? data,
        string? imageUrl)
    {
        // FCM caps SendEachForMulticastAsync at 500 tokens per call. For our
        // expected fan-out (<= a few per customer) this is overkill, but we
        // still chunk so admin broadcasts to a topic-less audience scale.
        const int batchSize = 500;
        var totalSuccess = 0;
        var staleTokens = new List<string>();

        for (var i = 0; i < rows.Count; i += batchSize)
        {
            var slice = rows.Skip(i).Take(batchSize).ToList();
            var message = new MulticastMessage
            {
                Tokens = slice.Select(r => r.FcmToken).ToList(),
                Notification = new Notification
                {
                    Title = title,
                    Body = body,
                    ImageUrl = imageUrl,
                },
                Data = data?.ToDictionary(kv => kv.Key, kv => kv.Value),
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ChannelId = "default",
                        Sound = "default",
                    },
                },
                Apns = new ApnsConfig
                {
                    Aps = new Aps
                    {
                        Sound = "default",
                        ContentAvailable = true,
                    },
                },
            };

            try
            {
                var response = await fcm.SendEachForMulticastAsync(message);
                totalSuccess += response.SuccessCount;

                for (var j = 0; j < response.Responses.Count; j++)
                {
                    var r = response.Responses[j];
                    if (r.IsSuccess) continue;
                    var code = r.Exception?.MessagingErrorCode;
                    if (code == MessagingErrorCode.Unregistered ||
                        code == MessagingErrorCode.InvalidArgument)
                    {
                        staleTokens.Add(slice[j].FcmToken);
                    }
                    else
                    {
                        _log.LogWarning(
                            "[Notify] FCM send failed (code={Code}): {Msg}",
                            code, r.Exception?.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[Notify] FCM batch send threw — skipping {Count} tokens.", slice.Count);
            }
        }

        if (staleTokens.Count > 0)
        {
            await _db.CustomerDeviceTokens
                .Where(t => staleTokens.Contains(t.FcmToken))
                .ExecuteDeleteAsync();
            _log.LogInformation("[Notify] Pruned {N} stale FCM tokens.", staleTokens.Count);
        }

        return totalSuccess;
    }

    // ─── Topics (broadcast) ─────────────────────────────────────────────

    /// <summary>
    /// Push to every device subscribed to <paramref name="topic"/>. Topic
    /// subscription is managed by the device itself (see the Flutter
    /// PushNotificationService) — the server just publishes.
    /// </summary>
    public async Task<bool> SendToTopicAsync(
        string topic,
        string title,
        string body,
        IDictionary<string, string>? data = null,
        string? imageUrl = null)
    {
        var fcm = Messaging;
        if (fcm == null)
        {
            _log.LogWarning("[Notify] Skipped topic '{Topic}' — Firebase not configured.", topic);
            return false;
        }

        // Topic names are bounded — sanitize to avoid an FCM 400.
        topic = SanitizeTopic(topic);
        if (string.IsNullOrEmpty(topic))
            throw new ArgumentException("Invalid topic name.", nameof(topic));

        var message = new Message
        {
            Topic = topic,
            Notification = new Notification
            {
                Title = title,
                Body = body,
                ImageUrl = imageUrl,
            },
            Data = data?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = "default",
                    Sound = "default",
                },
            },
            Apns = new ApnsConfig
            {
                Aps = new Aps { Sound = "default" },
            },
        };

        try
        {
            var msgId = await fcm.SendAsync(message);
            _log.LogInformation("[Notify] Broadcast to '{Topic}' sent (msgId={MsgId}).", topic, msgId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Notify] Topic broadcast failed: {Topic}", topic);
            return false;
        }
    }

    /// <summary>
    /// Subscribe every FCM token of a customer to a topic. The device-side
    /// service ALSO calls FirebaseMessaging.subscribeToTopic — this
    /// server-side path is for cases where the server is the source of
    /// truth (e.g. opting a user back in after admin action).
    /// </summary>
    public async Task<int> SubscribeCustomerToTopicAsync(int customerId, string topic)
    {
        var fcm = Messaging;
        if (fcm == null) return 0;

        topic = SanitizeTopic(topic);
        var tokens = await _db.CustomerDeviceTokens
            .Where(t => t.CustomerId == customerId)
            .Select(t => t.FcmToken)
            .ToListAsync();
        if (tokens.Count == 0) return 0;

        var response = await fcm.SubscribeToTopicAsync(tokens, topic);
        return response.SuccessCount;
    }

    public async Task<int> UnsubscribeCustomerFromTopicAsync(int customerId, string topic)
    {
        var fcm = Messaging;
        if (fcm == null) return 0;

        topic = SanitizeTopic(topic);
        var tokens = await _db.CustomerDeviceTokens
            .Where(t => t.CustomerId == customerId)
            .Select(t => t.FcmToken)
            .ToListAsync();
        if (tokens.Count == 0) return 0;

        var response = await fcm.UnsubscribeFromTopicAsync(tokens, topic);
        return response.SuccessCount;
    }

    // ─── Convenience wrappers for the events checkout fires ─────────────

    public Task SendOrderPlacedAsync(Order order)
    {
        if (order.CustomerId is null or 0) return Task.CompletedTask;
        var title = "Order placed";
        var body = $"Your order #{order.IncrementId} has been placed. Total ₹{order.GrandTotal:0.00}.";
        var data = new Dictionary<string, string>
        {
            ["type"] = "order.placed",
            ["orderId"] = order.Id.ToString(),
            ["incrementId"] = order.IncrementId ?? "",
        };
        return SendToCustomerAsync(order.CustomerId.Value, title, body, data);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// FCM topic names must match <c>[a-zA-Z0-9-_.~%]+</c> and be ≤ 900
    /// chars. We lowercase + strip anything else.
    /// </summary>
    private static string SanitizeTopic(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        var chars = s.Where(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '~' || c == '%').ToArray();
        var sanitized = new string(chars);
        return sanitized.Length > 900 ? sanitized[..900] : sanitized;
    }
}
