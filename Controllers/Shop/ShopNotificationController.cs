using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// In-app notification inbox for the logged-in customer — the history
/// behind whatever NotificationService.PersistAsync has written (order
/// events, admin broadcasts/per-customer pushes). Guest sessions have no
/// customer_id, so there's no inbox for them; guests still receive the
/// actual push (see NotificationService.SendGuestOrderPlacedAsync), just
/// not an in-app history of it.
/// </summary>
[ApiController]
[Route("api/shop/notifications")]
[Tags("Notification")]
[Authorize]
public class ShopNotificationController : ControllerBase
{
    private readonly DOSDbContext _db;

    // How far back the inbox looks. Older rows aren't deleted (harmless to
    // keep for analytics/debugging) — just excluded from the list query.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    public ShopNotificationController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List notifications from the last 30 days for the logged-in customer</summary>
    [HttpGet]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var cutoff = DateTime.UtcNow - RetentionWindow;
        var query = _db.Notifications
            .Where(n => n.CreatedAt >= cutoff && (n.CustomerId == customerId || n.CustomerId == null))
            .AsNoTracking();

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = rows.Select(n => new
        {
            id = n.Id,
            title = n.Title,
            body = n.Body,
            type = n.Type,
            data = n.DataJson,
            imageUrl = n.ImageUrl,
            // CreatedAt is stored as DateTime.UtcNow, but MySQL DATETIME
            // columns carry no timezone info, so EF Core reads it back as
            // DateTimeKind.Unspecified. Left as-is, System.Text.Json would
            // serialize it without a "Z" suffix, and clients would parse it
            // as local time instead of UTC — understating "time ago" by
            // the client's UTC offset. SpecifyKind restores the UTC marker
            // so the JSON carries "Z" and clients convert it correctly.
            createdAt = DateTime.SpecifyKind(n.CreatedAt, DateTimeKind.Utc),
            // Broadcast rows (CustomerId null) have no meaningful read state
            // for "this" customer — see the comment on NotificationRecord.IsRead.
            // Always reporting them read keeps them out of the unread badge
            // without needing a per-customer join table for what's currently
            // a low-volume promotional path.
            isRead = n.CustomerId == null || n.IsRead,
        });

        return Ok(new
        {
            data,
            meta = new
            {
                total,
                currentPage = page,
                perPage = limit,
                lastPage = (int)Math.Ceiling(total / (double)limit),
            },
        });
    }

    /// <summary>
    /// Count of unread personal notifications (last 30 days) — cheap enough
    /// to call on every Home screen load to drive the bell icon's dot.
    /// Broadcast rows never count towards this (see GetNotifications).
    /// </summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var cutoff = DateTime.UtcNow - RetentionWindow;
        var count = await _db.Notifications
            .Where(n => n.CreatedAt >= cutoff && n.CustomerId == customerId && !n.IsRead)
            .CountAsync();

        return Ok(new { data = new { unread = count } });
    }

    /// <summary>
    /// Marks every currently-unread personal notification as read — called
    /// when the inbox screen opens, so the dot clears the same way it would
    /// in Gmail/Instagram. Broadcast rows are left untouched (see
    /// NotificationRecord.IsRead).
    /// </summary>
    [HttpPost("mark-read")]
    public async Task<IActionResult> MarkAllRead()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var now = DateTime.UtcNow;
        var updated = await _db.Notifications
            .Where(n => n.CustomerId == customerId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now));

        return Ok(new { success = true, updated });
    }
}
