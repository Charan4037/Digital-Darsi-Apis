using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Customer;
using DOSApi.Services;

namespace DOSApi.Controllers;

/// <summary>
/// Customer-side endpoints for managing FCM push-notification device tokens.
/// The Flutter client calls these on app boot (after login) to register the
/// current device, on token-refresh, and on logout.
/// </summary>
[ApiController]
[Route("api/v1/customer/devices")]
[Authorize]
[Tags("Customer Devices")]
[ApiExplorerSettings(IgnoreApi = true)]
public class DeviceTokenController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly AuthService _auth;
    private readonly NotificationService _notify;

    public DeviceTokenController(DOSDbContext db, AuthService auth, NotificationService notify)
    {
        _db = db;
        _auth = auth;
        _notify = notify;
    }

    public record RegisterDeviceRequest(
        string Token,
        string? Platform,
        string? DeviceId,
        string? AppVersion,
        string? BuildNumber,
        string? DeviceModel,
        string? Manufacturer,
        string? OsVersion,
        string? Locale,
        string? Timezone);

    public record UnregisterDeviceRequest(string Token);

    /// <summary>
    /// Register or refresh an FCM token for the current customer. Idempotent —
    /// the same token may be re-posted on every boot to bump <c>last_seen_at</c>;
    /// if it was previously bound to a different customer (shared device,
    /// account switch) we re-bind it to the caller.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest req)
    {
        var customerId = _auth.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { message = "Token is required." });

        var now = DateTime.UtcNow;
        var existing = await _db.CustomerDeviceTokens
            .FirstOrDefaultAsync(t => t.FcmToken == req.Token);

        if (existing == null)
        {
            _db.CustomerDeviceTokens.Add(new CustomerDeviceToken
            {
                CustomerId = customerId.Value,
                FcmToken = req.Token,
                Platform = Trim(req.Platform, 16),
                DeviceId = Trim(req.DeviceId, 128),
                AppVersion = Trim(req.AppVersion, 32),
                BuildNumber = Trim(req.BuildNumber, 32),
                DeviceModel = Trim(req.DeviceModel, 128),
                Manufacturer = Trim(req.Manufacturer, 64),
                OsVersion = Trim(req.OsVersion, 64),
                Locale = Trim(req.Locale, 16),
                Timezone = Trim(req.Timezone, 64),
                LastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.CustomerId = customerId.Value;
            existing.Platform = Trim(req.Platform, 16) ?? existing.Platform;
            existing.DeviceId = Trim(req.DeviceId, 128) ?? existing.DeviceId;
            existing.AppVersion = Trim(req.AppVersion, 32) ?? existing.AppVersion;
            existing.BuildNumber = Trim(req.BuildNumber, 32) ?? existing.BuildNumber;
            existing.DeviceModel = Trim(req.DeviceModel, 128) ?? existing.DeviceModel;
            existing.Manufacturer = Trim(req.Manufacturer, 64) ?? existing.Manufacturer;
            existing.OsVersion = Trim(req.OsVersion, 64) ?? existing.OsVersion;
            existing.Locale = Trim(req.Locale, 16) ?? existing.Locale;
            existing.Timezone = Trim(req.Timezone, 64) ?? existing.Timezone;
            existing.LastSeenAt = now;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Device registered." });
    }

    /// <summary>
    /// Drop the device token (called on logout). Returns 200 even when the
    /// token wasn't on file so the client doesn't trip on idempotency.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Unregister([FromBody] UnregisterDeviceRequest req)
    {
        var customerId = _auth.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { message = "Token is required." });

        await _db.CustomerDeviceTokens
            .Where(t => t.FcmToken == req.Token && t.CustomerId == customerId.Value)
            .ExecuteDeleteAsync();

        return Ok(new { success = true, message = "Device unregistered." });
    }

    /// <summary>
    /// Subscribe every device of the current customer to the given topic.
    /// The Flutter client also calls FirebaseMessaging.subscribeToTopic
    /// directly — this server-side path is here so admin tooling can opt
    /// users in/out without touching the device.
    /// </summary>
    [HttpPost("topics/{topic}")]
    public async Task<IActionResult> Subscribe(string topic)
    {
        var customerId = _auth.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var n = await _notify.SubscribeCustomerToTopicAsync(customerId.Value, topic);
        return Ok(new { success = true, subscribed = n });
    }

    [HttpDelete("topics/{topic}")]
    public async Task<IActionResult> Unsubscribe(string topic)
    {
        var customerId = _auth.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var n = await _notify.UnsubscribeCustomerFromTopicAsync(customerId.Value, topic);
        return Ok(new { success = true, unsubscribed = n });
    }

    /// Clamp a client-supplied string to the column's max width. Returns
    /// null when the input is null/whitespace so we don't overwrite a
    /// non-null existing value with an empty string on a partial refresh.
    private static string? Trim(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > maxLen ? v[..maxLen] : v;
    }

    /// <summary>
    /// Diagnostic: send a test push to every device of the calling customer.
    /// Returns how many tokens we tried to send to and how many succeeded —
    /// useful for verifying the end-to-end FCM path without having to place
    /// a real order. Tokens that FCM rejects are pruned the same way the
    /// order-placed fan-out does it.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        var customerId = _auth.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var registered = await _db.CustomerDeviceTokens
            .CountAsync(t => t.CustomerId == customerId.Value);

        var sent = await _notify.SendToCustomerAsync(
            customerId.Value,
            "Digital Darsi",
            "This is a test push. If you can read this, notifications work.",
            new Dictionary<string, string> { ["type"] = "test" });

        return Ok(new
        {
            success = sent > 0,
            registeredDevices = registered,
            successfulSends = sent,
            message = registered == 0
                ? "No device tokens registered for this customer yet."
                : sent == 0
                    ? "All registered tokens were rejected by FCM."
                    : "Test push sent."
        });
    }
}
