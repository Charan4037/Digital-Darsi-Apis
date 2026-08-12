using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Customer;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin-side endpoints for managing FCM push-notification device tokens —
/// the staff counterpart to <see cref="DeviceTokenController"/>. The admin
/// panel/app calls these on login (after AdminBaseController confirms the
/// caller is an Admin or Super Admin) so order-placed pushes have somewhere
/// to land.
/// </summary>
[Route("api/v1/admin/devices")]
[Tags("Admin Devices")]
public class AdminDeviceTokenController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly NotificationService _notify;

    public AdminDeviceTokenController(DOSDbContext db, IConfiguration config, NotificationService notify)
        : base(db, config)
    {
        _db = db;
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
    /// Register or refresh an FCM token for the calling admin. Idempotent —
    /// safe to re-post on every app boot to bump <c>last_seen_at</c>.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest req)
    {
        if (!await IsAdminAsync()) return AdminUnauthorized();
        var customerId = CurrentCustomerId();
        if (!customerId.HasValue) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { message = "Token is required." });

        var now = DateTime.UtcNow;
        var existing = await _db.AdminDeviceTokens
            .FirstOrDefaultAsync(t => t.FcmToken == req.Token);

        if (existing == null)
        {
            _db.AdminDeviceTokens.Add(new AdminDeviceToken
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

    /// <summary>Drop the device token (called on admin logout).</summary>
    [HttpDelete]
    public async Task<IActionResult> Unregister([FromBody] UnregisterDeviceRequest req)
    {
        if (!await IsAdminAsync()) return AdminUnauthorized();
        var customerId = CurrentCustomerId();
        if (!customerId.HasValue) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { message = "Token is required." });

        await _db.AdminDeviceTokens
            .Where(t => t.FcmToken == req.Token && t.CustomerId == customerId.Value)
            .ExecuteDeleteAsync();

        return Ok(new { success = true, message = "Device unregistered." });
    }

    /// <summary>
    /// Diagnostic: send a test push to every device of the calling admin —
    /// verifies the end-to-end FCM path without waiting for a real order.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        if (!await IsAdminAsync()) return AdminUnauthorized();
        var customerId = CurrentCustomerId();
        if (!customerId.HasValue) return AdminUnauthorized();

        var registered = await _db.AdminDeviceTokens.CountAsync(t => t.CustomerId == customerId.Value);
        var sent = await _notify.SendToAdminAsync(
            customerId.Value,
            "Digital Darsi Admin",
            "This is a test push. If you can read this, admin notifications work.",
            new Dictionary<string, string> { ["type"] = "admin.test" });

        return Ok(new
        {
            success = sent > 0,
            registeredDevices = registered,
            successfulSends = sent,
            message = registered == 0
                ? "No device tokens registered for this admin yet."
                : sent == 0
                    ? "All registered tokens were rejected by FCM."
                    : "Test push sent."
        });
    }

    private static string? Trim(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > maxLen ? v[..maxLen] : v;
    }
}
