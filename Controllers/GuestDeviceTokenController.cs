using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Customer;

namespace BagistoApi.Controllers;

/// <summary>
/// Lets a guest (unauthenticated) device register its FCM token so it can
/// receive push notifications after placing a guest order.
///
/// The Flutter client should call POST /api/v1/guest/devices immediately
/// after FCM initializes, passing the X-Session-Token header that it already
/// sends on every API request. When the order is placed the backend looks
/// up the FCM token by that same session token and fires the notification.
///
/// Tokens expire after 7 days — well past any checkout window.
/// </summary>
[ApiController]
[Route("api/v1/guest/devices")]
[Tags("Guest Devices")]
[ApiExplorerSettings(IgnoreApi = true)]
public class GuestDeviceTokenController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public GuestDeviceTokenController(BagistoDbContext db) => _db = db;

    public record RegisterGuestDeviceRequest(
        string Token,
        string? Platform = null,
        string? DeviceId = null,
        string? AppVersion = null,
        string? DeviceModel = null);

    /// <summary>
    /// Register or refresh an FCM token for the current guest session.
    /// Idempotent — re-posting the same session token updates the FCM token
    /// and bumps expiry.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Register(
        [FromHeader(Name = "X-Session-Token")] string? sessionToken,
        [FromBody] RegisterGuestDeviceRequest req)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
            return BadRequest(new { message = "X-Session-Token header is required." });
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { message = "Token is required." });

        // Clean up expired tokens while we're here (best-effort, no failure impact).
        await _db.GuestDeviceTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        var expiry = now.AddDays(7);

        var existing = await _db.GuestDeviceTokens
            .FirstOrDefaultAsync(t => t.SessionToken == sessionToken);

        if (existing == null)
        {
            _db.GuestDeviceTokens.Add(new GuestDeviceToken
            {
                SessionToken = sessionToken,
                FcmToken = req.Token,
                Platform = Trim(req.Platform, 16),
                DeviceId = Trim(req.DeviceId, 128),
                AppVersion = Trim(req.AppVersion, 32),
                DeviceModel = Trim(req.DeviceModel, 128),
                ExpiresAt = expiry,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.FcmToken = req.Token;
            existing.Platform = Trim(req.Platform, 16) ?? existing.Platform;
            existing.DeviceId = Trim(req.DeviceId, 128) ?? existing.DeviceId;
            existing.AppVersion = Trim(req.AppVersion, 32) ?? existing.AppVersion;
            existing.DeviceModel = Trim(req.DeviceModel, 128) ?? existing.DeviceModel;
            existing.ExpiresAt = expiry;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Guest device registered." });
    }

    private static string? Trim(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > maxLen ? v[..maxLen] : v;
    }
}
