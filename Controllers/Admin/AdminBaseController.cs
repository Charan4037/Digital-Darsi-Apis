using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Shared base for all admin controllers.
/// Admin endpoints are public routes ([AllowAnonymous]) that enforce
/// their own auth via either the caller's customer JWT (role claim "admin"
/// plus a per-request RBAC lookup) or the X-Admin-Key header — same pattern
/// as AdminNotificationController. There is no separate admin login.
/// </summary>
[ApiController]
[AllowAnonymous]
public abstract class AdminBaseController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly IConfiguration _config;

    // Short-TTL in-memory cache for the RBAC lookups below — same pattern as
    // OrderInvoiceService's PDF cache. Without this, every single admin API
    // call does a full DB round trip just to check permissions (measured at
    // ~2.3s against the real prod DB's latency), on top of whatever the
    // endpoint's own query costs — the single biggest contributor to the
    // admin app feeling slow on every screen, not just Products. 30s keeps
    // permission changes propagating quickly (still well within "immediately"
    // for a human clicking through the Roles UI) while eliminating that
    // round trip on every subsequent request in a normal browsing session.
    private static readonly ConcurrentDictionary<int, (bool isAdmin, DateTime expiresAt)> _isAdminCache = new();
    private static readonly ConcurrentDictionary<string, (bool canRead, bool canWrite, DateTime expiresAt)> _permissionCache = new();
    private static readonly TimeSpan PermissionCacheTtl = TimeSpan.FromSeconds(30);

    protected AdminBaseController(DOSDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool IsAdminKeyValid()
    {
        var expected = _config["Admin:NotificationApiKey"];
        if (string.IsNullOrEmpty(expected)) return false;
        var supplied = Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(supplied);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    protected int? CurrentCustomerId()
    {
        if (User.Identity?.IsAuthenticated != true) return null;
        var claim = User.FindFirst("customer_id")?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// Coarse "is this caller an admin at all" check, regardless of which
    /// specific feature they can touch. Used for landing pages (e.g. the
    /// dashboard summary) that should be visible to every admin role.
    /// </summary>
    protected async Task<bool> IsAdminAsync()
    {
        if (IsAdminKeyValid()) return true;

        var customerId = CurrentCustomerId();
        if (customerId == null) return false;

        var now = DateTime.UtcNow;
        if (_isAdminCache.TryGetValue(customerId.Value, out var cached) && now < cached.expiresAt)
            return cached.isAdmin;

        var isAdmin = await _db.CustomerAdmins
            .AnyAsync(a => a.CustomerId == customerId && a.RoleId != null);
        _isAdminCache[customerId.Value] = (isAdmin, now.Add(PermissionCacheTtl));
        return isAdmin;
    }

    /// <summary>
    /// Granular permission check for a specific feature (see the
    /// `permissions` table / RbacSeeder for the full catalog). Does a
    /// per-request DB lookup rather than trusting a JWT claim, so that
    /// permission changes made via the Roles UI take effect immediately —
    /// not just after the caller's access token expires.
    /// </summary>
    protected async Task<bool> HasPermissionAsync(string featureKey, bool requireWrite = false)
    {
        if (IsAdminKeyValid()) return true;

        var customerId = CurrentCustomerId();
        if (customerId == null) return false;

        var cacheKey = $"{customerId}:{featureKey}";
        var now = DateTime.UtcNow;
        if (_permissionCache.TryGetValue(cacheKey, out var cached) && now < cached.expiresAt)
            return requireWrite ? cached.canWrite : cached.canRead;

        var grant = await _db.CustomerAdmins
            .Where(a => a.CustomerId == customerId && a.RoleId != null)
            .Join(_db.RolePermissions, a => a.RoleId, rp => rp.RoleId, (a, rp) => rp)
            .Join(_db.Permissions, rp => rp.PermissionId, p => p.Id, (rp, p) => new { rp.CanRead, rp.CanWrite, p.FeatureKey })
            .Where(x => x.FeatureKey == featureKey)
            .Select(x => new { x.CanRead, x.CanWrite })
            .FirstOrDefaultAsync();

        var canRead = grant?.CanRead ?? false;
        var canWrite = grant?.CanWrite ?? false;
        _permissionCache[cacheKey] = (canRead, canWrite, now.Add(PermissionCacheTtl));

        return requireWrite ? canWrite : canRead;
    }

    /// <summary>Not an admin at all (no valid session / admin key).</summary>
    protected IActionResult AdminUnauthorized() =>
        Unauthorized(new { success = false, message = "Admin key missing or invalid." });

    /// <summary>Admin, but their role lacks this specific permission.</summary>
    protected IActionResult AdminForbidden(string feature) =>
        StatusCode(403, new { success = false, message = $"You do not have permission to modify {feature}." });

    protected static string Slugify(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "-");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"-+", "-");
        return s.Trim('-');
    }
}
