using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for homepage promotional banners.
/// Routes: /api/v1/admin/banners
///
/// Banners are stored in the dd_scraped_banners table (ScrapedBanner model).
/// site_key maps to the four stores: foodstore, buildstore, store, services.
/// </summary>
[Route("api/v1/admin/banners")]
[Tags("Admin – Banners")]
public class AdminBannerController : AdminBaseController
{
    private readonly BagistoDbContext _db;
    private readonly FirebaseStorageService _storage;

    private static readonly string[] ValidSiteKeys =
        { "foodstore", "buildstore", "store", "services" };

    public AdminBannerController(
        BagistoDbContext db,
        FirebaseStorageService storage,
        IConfiguration config) : base(config)
    {
        _db      = db;
        _storage = storage;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all banners, optionally filtered by site_key.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? siteKey = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var query = _db.ScrapedBanners.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(siteKey))
            query = query.Where(b => b.SiteKey == siteKey);

        var banners = await query.OrderBy(b => b.SiteKey).ThenBy(b => b.SortOrder).ToListAsync();
        return Ok(new { success = true, data = banners });
    }

    // ─── Get single ───────────────────────────────────────────────────────

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var banner = await _db.ScrapedBanners.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (banner == null) return NotFound(new { success = false, message = "Banner not found." });
        return Ok(new { success = true, data = banner });
    }

    // ─── Create ───────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new banner. Image is required.
    /// site_key must be one of: foodstore, buildstore, store, services.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create(
        [FromForm] string siteKey,
        [FromForm] string? title       = null,
        [FromForm] string? subtitle    = null,
        [FromForm] string? linkUrl     = null,
        [FromForm] int sortOrder       = 0,
        IFormFile? image               = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        if (string.IsNullOrWhiteSpace(siteKey))
            return BadRequest(new { success = false, message = "site_key is required." });
        if (!ValidSiteKeys.Contains(siteKey))
            return BadRequest(new { success = false, message = $"site_key must be one of: {string.Join(", ", ValidSiteKeys)}" });
        if (image == null || image.Length == 0)
            return BadRequest(new { success = false, message = "image is required." });

        var imageUrl = await UploadBannerImageAsync(image, siteKey, sortOrder);

        var banner = new ScrapedBanner
        {
            SiteKey   = siteKey,
            ImageUrl  = imageUrl,
            Title     = title,
            Subtitle  = subtitle,
            LinkUrl   = linkUrl,
            SortOrder = sortOrder,
        };
        _db.ScrapedBanners.Add(banner);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = banner.Id },
            new { success = true, message = "Banner created.", data = banner });
    }

    // ─── Update ───────────────────────────────────────────────────────────

    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Update(
        int id,
        [FromForm] string? siteKey     = null,
        [FromForm] string? title       = null,
        [FromForm] string? subtitle    = null,
        [FromForm] string? linkUrl     = null,
        [FromForm] int? sortOrder      = null,
        IFormFile? image               = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var banner = await _db.ScrapedBanners.FindAsync(id);
        if (banner == null) return NotFound(new { success = false, message = "Banner not found." });

        if (!string.IsNullOrWhiteSpace(siteKey))
        {
            if (!ValidSiteKeys.Contains(siteKey))
                return BadRequest(new { success = false, message = $"site_key must be one of: {string.Join(", ", ValidSiteKeys)}" });
            banner.SiteKey = siteKey;
        }
        if (title    != null) banner.Title     = title;
        if (subtitle != null) banner.Subtitle  = subtitle;
        if (linkUrl  != null) banner.LinkUrl   = linkUrl;
        if (sortOrder.HasValue) banner.SortOrder = sortOrder.Value;

        if (image != null && image.Length > 0)
            banner.ImageUrl = await UploadBannerImageAsync(image, banner.SiteKey, banner.SortOrder);

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Banner updated.", data = banner });
    }

    // ─── Reorder ──────────────────────────────────────────────────────────

    /// <summary>Bulk-update sort_order for banners in a site.</summary>
    [HttpPatch("reorder")]
    public async Task<IActionResult> Reorder([FromBody] List<BannerOrderItem> items)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (items == null || items.Count == 0)
            return BadRequest(new { success = false, message = "No items provided." });

        var ids     = items.Select(i => i.Id).ToList();
        var banners = await _db.ScrapedBanners.Where(b => ids.Contains(b.Id)).ToListAsync();
        foreach (var banner in banners)
        {
            var item = items.FirstOrDefault(i => i.Id == banner.Id);
            if (item != null) banner.SortOrder = item.SortOrder;
        }
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Sort order updated." });
    }

    public record BannerOrderItem(int Id, int SortOrder);

    // ─── Delete ───────────────────────────────────────────────────────────

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var banner = await _db.ScrapedBanners.FindAsync(id);
        if (banner == null) return NotFound(new { success = false, message = "Banner not found." });
        _db.ScrapedBanners.Remove(banner);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Banner deleted." });
    }

    // ─── Helper ───────────────────────────────────────────────────────────

    private async Task<string> UploadBannerImageAsync(IFormFile file, string siteKey, int sortOrder)
    {
        var ext         = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".jpg";
        var storagePath = $"banners/{siteKey}/{sortOrder}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        await using var stream = file.OpenReadStream();
        var (url, err) = await _storage.UploadFromStreamAsync(stream, storagePath, file.ContentType);
        if (err != null) throw new Exception($"Image upload failed: {err}");
        return url!;
    }
}
