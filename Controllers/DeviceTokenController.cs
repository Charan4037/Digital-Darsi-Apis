using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Customer;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

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
    private readonly BagistoDbContext _db;
    private readonly AuthService _auth;
    private readonly NotificationService _notify;

    public DeviceTokenController(BagistoDbContext db, AuthService auth, NotificationService notify)
    {
        _db = db;
        _auth = auth;
        _notify = notify;
    }

    public record RegisterDeviceRequest(string Token, string? Platform, string? DeviceId, string? AppVersion);
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
                Platform = req.Platform,
                DeviceId = req.DeviceId,
                AppVersion = req.AppVersion,
                LastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.CustomerId = customerId.Value;
            existing.Platform = req.Platform ?? existing.Platform;
            existing.DeviceId = req.DeviceId ?? existing.DeviceId;
            existing.AppVersion = req.AppVersion ?? existing.AppVersion;
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
}
