using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

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
    private readonly DOSDbContext _db;
    private readonly FirebaseStorageService _storage;

    private static readonly string[] ValidSiteKeys =
        { "foodstore", "buildstore", "store", "services" };

    private static readonly string[] ValidLinkTypes =
        { "none", "url", "category", "product", "vendor" };

    public AdminBannerController(
        DOSDbContext db,
        FirebaseStorageService storage,
        IConfiguration config) : base(db, config)
    {
        _db      = db;
        _storage = storage;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all banners</summary>
    /// <remarks>
    /// Returns all promotional banners. Optionally filter by store section.
    ///
    /// **site_key values and which screen they appear on:**
    /// - `foodstore` — FoodStore home screen carousel
    /// - `buildstore` — BuildStore home screen carousel
    /// - `store` — General Store home screen carousel
    /// - `services` — Services section carousel
    ///
    /// **Example:** To see only FoodStore banners → `?siteKey=foodstore`
    /// </remarks>
    /// <param name="siteKey">Filter by store: foodstore | buildstore | store | services</param>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? siteKey = null)
    {
        if (!await HasPermissionAsync("banners")) return AdminUnauthorized();

        var query = _db.ScrapedBanners.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(siteKey))
            query = query.Where(b => b.SiteKey == siteKey);

        var banners = await query.OrderBy(b => b.SiteKey).ThenBy(b => b.SortOrder).ToListAsync();
        var data = new List<object>();
        foreach (var b in banners) data.Add(await ToDtoAsync(b));
        return Ok(new { success = true, data });
    }

    // ─── Get single ───────────────────────────────────────────────────────

    /// <summary>Get a single banner by ID</summary>
    /// <param name="id">Banner ID from the List endpoint</param>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("banners")) return AdminUnauthorized();
        var banner = await _db.ScrapedBanners.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (banner == null) return NotFound(new { success = false, message = "Banner not found." });
        return Ok(new { success = true, data = await ToDtoAsync(banner) });
    }

    // ─── Create ───────────────────────────────────────────────────────────

    /// <summary>Create a new promotional banner</summary>
    /// <remarks>
    /// Uploads a banner image to Firebase and adds it to the specified store's carousel.
    /// Send as **multipart/form-data** (required for image upload).
    ///
    /// **Field guide:**
    /// - `siteKey` — Which store screen to show this banner on (required)
    ///   - `foodstore` → FoodStore home screen
    ///   - `buildstore` → BuildStore home screen
    ///   - `store` → General Store home screen
    ///   - `services` → Services section
    /// - `title` — Main heading shown on the banner (e.g. "Fresh Vegetables")
    /// - `subtitle` — Supporting text below the title (e.g. "Delivered in 30 minutes")
    /// - `linkType` — What tapping the banner does: `none` | `url` | `category` | `product`
    /// - `linkUrl` — required when linkType is `url`
    /// - `categoryId` — required when linkType is `category`
    /// - `productId` — required when linkType is `product`
    /// - `vendorId` — required when linkType is `vendor`
    /// - `sortOrder` — Position in the carousel. 0 = first/leftmost. Default: 0
    /// - `image` — The banner image file (jpg, png, webp, gif) — **required**
    ///
    /// **Recommended image size:** 1200 × 450 px (landscape, wide format)
    /// </remarks>
    /// <param name="siteKey">Which store: foodstore | buildstore | store | services (required)</param>
    /// <param name="title">Banner headline text</param>
    /// <param name="subtitle">Supporting text below the title</param>
    /// <param name="linkType">What tapping the banner does: none | url | category | product. Default: none (or "url" if linkUrl is set, for backward compatibility)</param>
    /// <param name="linkUrl">URL to open when banner is tapped — required when linkType is "url"</param>
    /// <param name="categoryId">Target category id — required when linkType is "category"</param>
    /// <param name="productId">Target product id — required when linkType is "product"</param>
    /// <param name="vendorId">Target vendor id — required when linkType is "vendor"</param>
    /// <param name="sortOrder">Position in carousel — 0 is first. Default: 0</param>
    /// <param name="image">Banner image file — required (jpg, png, webp, gif)</param>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Create(
        [FromForm] string siteKey,
        [FromForm] string? title       = null,
        [FromForm] string? subtitle    = null,
        [FromForm] string? linkType    = null,
        [FromForm] string? linkUrl     = null,
        [FromForm] int? categoryId     = null,
        [FromForm] int? productId      = null,
        [FromForm] int? vendorId       = null,
        [FromForm] int sortOrder       = 0,
        IFormFile? image               = null)
    {
        if (!await HasPermissionAsync("banners", requireWrite: true)) return AdminForbidden("banners");

        if (string.IsNullOrWhiteSpace(siteKey))
            return BadRequest(new { success = false, message = "site_key is required." });
        if (!ValidSiteKeys.Contains(siteKey))
            return BadRequest(new { success = false, message = $"site_key must be one of: {string.Join(", ", ValidSiteKeys)}" });
        if (image == null || image.Length == 0)
            return BadRequest(new { success = false, message = "image is required." });

        var (resolvedLinkType, linkError) = await ResolveLinkAsync(linkType, linkUrl, categoryId, productId, vendorId);
        if (linkError != null) return BadRequest(new { success = false, message = linkError });

        var imageUrl = await UploadBannerImageAsync(image, siteKey, sortOrder);

        var banner = new ScrapedBanner
        {
            SiteKey    = siteKey,
            ImageUrl   = imageUrl,
            Title      = title,
            Subtitle   = subtitle,
            LinkType   = resolvedLinkType,
            LinkUrl    = resolvedLinkType == "url" ? linkUrl : null,
            CategoryId = resolvedLinkType == "category" ? categoryId : null,
            ProductId  = resolvedLinkType == "product" ? productId : null,
            VendorId   = resolvedLinkType == "vendor" ? vendorId : null,
            SortOrder  = sortOrder,
        };
        _db.ScrapedBanners.Add(banner);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = banner.Id },
            new { success = true, message = "Banner created.", data = await ToDtoAsync(banner) });
    }

    // ─── Update ───────────────────────────────────────────────────────────

    /// <summary>Update a banner's text or image</summary>
    /// <remarks>
    /// Updates an existing banner. Send as **multipart/form-data**.
    /// Only include fields you want to change — omitted fields keep their current values.
    /// To replace the image, attach a new file; leave the `image` field empty to keep the current image.
    /// </remarks>
    /// <param name="id">Banner ID to update</param>
    /// <param name="siteKey">Move banner to a different store section</param>
    /// <param name="title">New headline text</param>
    /// <param name="subtitle">New subtitle text</param>
    /// <param name="linkType">What tapping the banner does: none | url | category | product. Omit to leave unchanged.</param>
    /// <param name="linkUrl">New tap URL — required when linkType is "url"</param>
    /// <param name="categoryId">Target category id — required when linkType is "category"</param>
    /// <param name="productId">Target product id — required when linkType is "product"</param>
    /// <param name="vendorId">Target vendor id — required when linkType is "vendor"</param>
    /// <param name="sortOrder">New position in carousel</param>
    /// <param name="image">New image file (replaces existing)</param>
    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Update(
        int id,
        [FromForm] string? siteKey     = null,
        [FromForm] string? title       = null,
        [FromForm] string? subtitle    = null,
        [FromForm] string? linkType    = null,
        [FromForm] string? linkUrl     = null,
        [FromForm] int? categoryId     = null,
        [FromForm] int? productId      = null,
        [FromForm] int? vendorId       = null,
        [FromForm] int? sortOrder      = null,
        IFormFile? image               = null)
    {
        if (!await HasPermissionAsync("banners", requireWrite: true)) return AdminForbidden("banners");

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
        if (sortOrder.HasValue) banner.SortOrder = sortOrder.Value;

        if (linkType != null)
        {
            var (resolvedLinkType, linkError) = await ResolveLinkAsync(linkType, linkUrl, categoryId, productId, vendorId);
            if (linkError != null) return BadRequest(new { success = false, message = linkError });

            banner.LinkType   = resolvedLinkType;
            banner.LinkUrl    = resolvedLinkType == "url" ? linkUrl : null;
            banner.CategoryId = resolvedLinkType == "category" ? categoryId : null;
            banner.ProductId  = resolvedLinkType == "product" ? productId : null;
            banner.VendorId   = resolvedLinkType == "vendor" ? vendorId : null;
        }

        if (image != null && image.Length > 0)
            banner.ImageUrl = await UploadBannerImageAsync(image, banner.SiteKey, banner.SortOrder);

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Banner updated.", data = await ToDtoAsync(banner) });
    }

    // ─── Reorder ──────────────────────────────────────────────────────────

    /// <summary>Reorder multiple banners at once</summary>
    /// <remarks>
    /// Updates the carousel position of multiple banners in a single call.
    /// Send an array of `{ "id": 5, "sortOrder": 0 }` objects.
    ///
    /// **Example body — set banner 5 first, banner 8 second:**
    /// ```json
    /// [
    ///   { "id": 5, "sortOrder": 0 },
    ///   { "id": 8, "sortOrder": 1 }
    /// ]
    /// ```
    ///
    /// **Use this for:** Drag-and-drop reordering in the admin UI.
    /// </remarks>
    [HttpPatch("reorder")]
    public async Task<IActionResult> Reorder([FromBody] List<BannerOrderItem> items)
    {
        if (!await HasPermissionAsync("banners", requireWrite: true)) return AdminForbidden("banners");
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

    /// <summary>Delete a banner permanently</summary>
    /// <remarks>⚠️ This cannot be undone. The image in Firebase Storage is not deleted, only the database record.</remarks>
    /// <param name="id">Banner ID to delete</param>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync("banners", requireWrite: true)) return AdminForbidden("banners");
        var banner = await _db.ScrapedBanners.FindAsync(id);
        if (banner == null) return NotFound(new { success = false, message = "Banner not found." });
        _db.ScrapedBanners.Remove(banner);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Banner deleted." });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Validates linkType and its matching reference, resolving a null/omitted
    /// linkType to "url" or "none" the same way the legacy data did (so
    /// existing admin-client callers that only ever sent linkUrl keep working).
    /// </summary>
    private async Task<(string LinkType, string? Error)> ResolveLinkAsync(
        string? linkType, string? linkUrl, int? categoryId, int? productId, int? vendorId)
    {
        var resolved = string.IsNullOrWhiteSpace(linkType)
            ? (string.IsNullOrWhiteSpace(linkUrl) ? "none" : "url")
            : linkType;

        if (!ValidLinkTypes.Contains(resolved))
            return (resolved, $"linkType must be one of: {string.Join(", ", ValidLinkTypes)}");

        switch (resolved)
        {
            case "url":
                if (string.IsNullOrWhiteSpace(linkUrl))
                    return (resolved, "linkUrl is required when linkType is \"url\".");
                break;
            case "category":
                if (!categoryId.HasValue)
                    return (resolved, "categoryId is required when linkType is \"category\".");
                if (!await _db.Categories.AnyAsync(c => c.Id == categoryId.Value))
                    return (resolved, $"Category id {categoryId} not found.");
                break;
            case "product":
                if (!productId.HasValue)
                    return (resolved, "productId is required when linkType is \"product\".");
                if (!await _db.Products.AnyAsync(p => p.Id == productId.Value))
                    return (resolved, $"Product id {productId} not found.");
                break;
            case "vendor":
                if (!vendorId.HasValue)
                    return (resolved, "vendorId is required when linkType is \"vendor\".");
                if (!await _db.Vendors.AnyAsync(v => v.Id == vendorId.Value))
                    return (resolved, $"Vendor id {vendorId} not found.");
                break;
        }

        return (resolved, null);
    }

    /// <summary>Adds a human-readable category/product name for the admin UI's
    /// selected-link display — the raw entity only carries ids.</summary>
    private async Task<object> ToDtoAsync(ScrapedBanner b)
    {
        string? categoryName = null;
        string? productName  = null;
        string? vendorName   = null;

        if (b.CategoryId.HasValue)
            categoryName = await _db.Categories
                .Where(c => c.Id == b.CategoryId.Value)
                .Select(c => c.Translations.FirstOrDefault(t => t.Locale == "en") != null
                    ? c.Translations.First(t => t.Locale == "en").Name
                    : c.Translations.FirstOrDefault()!.Name)
                .FirstOrDefaultAsync();

        if (b.ProductId.HasValue)
            productName = await _db.ProductFlats
                .Where(f => f.ProductId == b.ProductId.Value)
                .OrderByDescending(f => f.Locale == "en")
                .Select(f => f.Name)
                .FirstOrDefaultAsync();

        if (b.VendorId.HasValue)
            vendorName = await _db.Vendors
                .Where(v => v.Id == b.VendorId.Value)
                .Select(v => v.Name)
                .FirstOrDefaultAsync();

        return new
        {
            b.Id,
            b.SiteKey,
            b.ImageUrl,
            b.Title,
            b.Subtitle,
            LinkType = b.LinkType ?? (string.IsNullOrWhiteSpace(b.LinkUrl) ? "none" : "url"),
            b.LinkUrl,
            b.CategoryId,
            CategoryName = categoryName,
            b.ProductId,
            ProductName = productName,
            b.VendorId,
            VendorName = vendorName,
            b.SortOrder,
        };
    }

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
