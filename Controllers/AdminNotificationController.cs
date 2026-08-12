using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;

namespace DOSApi.Controllers;

/// <summary>
/// Admin push-notification surface. There is no customer-role system yet,
/// so access is gated on a static API key supplied via the
/// <c>X-Admin-Key</c> header. The expected value comes from
/// <c>Admin:NotificationApiKey</c> in configuration — set it per-environment
/// and rotate it like any other secret.
///
/// <c>[AllowAnonymous]</c> is intentional: the global default-deny policy
/// requires a JWT-authenticated principal, but admin tooling won't have
/// one. We enforce auth via the API key check inside each action.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications")]
[AllowAnonymous]
[Tags("Admin Notifications")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AdminNotificationController : ControllerBase
{
    private readonly NotificationService _notify;
    private readonly IConfiguration _config;

    public AdminNotificationController(NotificationService notify, IConfiguration config)
    {
        _notify = notify;
        _config = config;
    }

    public record BroadcastRequest(
        string Topic,
        string Title,
        string Body,
        string? ImageUrl = null,
        Dictionary<string, string>? Data = null);

    public record CustomerNotifyRequest(
        string Title,
        string Body,
        string? ImageUrl = null,
        Dictionary<string, string>? Data = null);

    /// <summary>
    /// Broadcast a notification to every device subscribed to <c>topic</c>.
    /// Use cases: promotional campaigns, app-wide announcements.
    /// </summary>
    [HttpPost("broadcast")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest req)
    {
        if (!IsAdmin()) return Unauthorized(new { message = "Admin key missing or invalid." });
        if (string.IsNullOrWhiteSpace(req.Topic) ||
            string.IsNullOrWhiteSpace(req.Title) ||
            string.IsNullOrWhiteSpace(req.Body))
        {
            return BadRequest(new { message = "Topic, title, and body are required." });
        }

        var ok = await _notify.SendToTopicAsync(req.Topic, req.Title, req.Body, req.Data, req.ImageUrl);
        return ok
            ? Ok(new { success = true, message = $"Broadcast sent to '{req.Topic}'." })
            : StatusCode(503, new { success = false, message = "Notification service unavailable." });
    }

    /// <summary>
    /// Push a notification to one specific customer (every device they own).
    /// Useful for transactional events the regular checkout flow doesn't
    /// cover yet — e.g. order shipped, refund issued.
    /// </summary>
    [HttpPost("customer/{customerId:int}")]
    public async Task<IActionResult> NotifyCustomer(int customerId, [FromBody] CustomerNotifyRequest req)
    {
        if (!IsAdmin()) return Unauthorized(new { message = "Admin key missing or invalid." });
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new { message = "Title and body are required." });

        var sent = await _notify.SendToCustomerAsync(customerId, req.Title, req.Body, req.Data, req.ImageUrl);
        return Ok(new { success = true, sent });
    }

    /// <summary>
    /// Push a notification to every device belonging to every Admin/Super
    /// Admin staff account. Diagnostic/ops tool for the staff broadcast path
    /// (the same path <c>CheckoutService.PlaceOrderAsync</c> fires
    /// automatically on every new order) — lets it be verified without
    /// placing a real order.
    /// </summary>
    [HttpPost("admins")]
    public async Task<IActionResult> NotifyAdmins([FromBody] CustomerNotifyRequest req)
    {
        if (!IsAdmin()) return Unauthorized(new { message = "Admin key missing or invalid." });
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new { message = "Title and body are required." });

        var sent = await _notify.SendToAdminsAsync(req.Title, req.Body, req.Data, req.ImageUrl);
        return Ok(new { success = true, sent });
    }

    private bool IsAdmin()
    {
        var expected = _config["Admin:NotificationApiKey"];
        if (string.IsNullOrEmpty(expected)) return false;
        var supplied = Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied)) return false;

        // FixedTimeEquals requires equal-length inputs. Pad the shorter
        // side so a wrong-length supplied key doesn't throw, and still
        // compare in constant time relative to the expected length.
        var a = System.Text.Encoding.UTF8.GetBytes(supplied);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
