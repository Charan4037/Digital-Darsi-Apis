using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Shared base for all admin controllers.
/// Admin endpoints are public routes ([AllowAnonymous]) that enforce
/// their own auth via the X-Admin-Key header — same pattern as
/// AdminNotificationController. There is no customer JWT involved.
/// </summary>
[ApiController]
[AllowAnonymous]
public abstract class AdminBaseController : ControllerBase
{
    private readonly IConfiguration _config;

    protected AdminBaseController(IConfiguration config)
    {
        _config = config;
    }

    protected bool IsAdmin()
    {
        // Preferred path: the caller's own customer JWT carries the "admin"
        // role claim (see AuthService.GenerateAccessToken). This is what the
        // Flutter app uses — it never ships or sends a static admin secret.
        if (User.Identity?.IsAuthenticated == true && User.IsInRole("admin"))
            return true;

        // Fallback path: static X-Admin-Key header, for server-to-server/
        // tooling use where there's no customer JWT to attach.
        var expected = _config["Admin:NotificationApiKey"];
        if (string.IsNullOrEmpty(expected)) return false;
        var supplied = Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(supplied);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    protected IActionResult AdminUnauthorized() =>
        Unauthorized(new { success = false, message = "Admin key missing or invalid." });

    protected static string Slugify(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "-");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"-+", "-");
        return s.Trim('-');
    }
}
