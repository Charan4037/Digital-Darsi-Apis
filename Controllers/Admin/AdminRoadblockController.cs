using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for the home-screen "roadblock" popup.
/// Routes: /api/v1/admin/roadblocks
///
/// Popups are stored in the dd_roadblock_popups table (RoadblockPopup model)
/// and are global — shown on the home screen regardless of which store
/// section is selected, unlike banners. Any number of rows may be active
/// (IsActive) at once — the public endpoint serves ALL active rows, ordered
/// by sort_order/id, and the app shows them one after another, each
/// dismissed before the next appears.
/// </summary>
[Route("api/v1/admin/roadblocks")]
[Tags("Admin – Roadblock Popup")]
public class AdminRoadblockController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly FirebaseStorageService _storage;

    private static readonly string[] ValidLinkTypes =
        { "none", "url", "category", "product", "vendor" };

    public AdminRoadblockController(
        DOSDbContext db,
        FirebaseStorageService storage,
        IConfiguration config) : base(db, config)
    {
        _db      = db;
        _storage = storage;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all roadblock popups</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!await HasPermissionAsync("roadblocks")) return AdminUnauthorized();

        var popups = await _db.RoadblockPopups.AsNoTracking().OrderBy(p => p.SortOrder).ToListAsync();
        var data = new List<object>();
        foreach (var p in popups) data.Add(await ToDtoAsync(p));
        return Ok(new { success = true, data });
    }

    // ─── Get single ───────────────────────────────────────────────────────

    /// <summary>Get a single roadblock popup by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("roadblocks")) return AdminUnauthorized();
        var popup = await _db.RoadblockPopups.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (popup == null) return NotFound(new { success = false, message = "Roadblock popup not found." });
        return Ok(new { success = true, data = await ToDtoAsync(popup) });
    }

    // ─── Create ───────────────────────────────────────────────────────────

    /// <summary>Create a new roadblock popup</summary>
    /// <remarks>
    /// Uploads a popup image to Firebase and adds it as a global home-screen
    /// popup (shown regardless of store section). Send as
    /// **multipart/form-data** (required for image upload). The whole
    /// creative (heading, price, CTA button) should be baked into the image —
    /// there is no separate title/subtitle field.
    ///
    /// **Field guide:**
    /// - `linkType` — What tapping the popup does: `none` | `url` | `category` | `product` | `vendor`
    /// - `linkUrl` — required when linkType is `url`
    /// - `categoryId` — required when linkType is `category`
    /// - `productId` — required when linkType is `product`
    /// - `vendorId` — required when linkType is `vendor`
    /// - `sortOrder` — Display order when more than one popup is active (lowest first). Default: 0
    /// - `isActive` — Whether this popup is shown. Default: true
    /// - `image` — The popup image file (jpg, png, webp, gif) — **required**
    /// </remarks>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Create(
        [FromForm] string? linkType    = null,
        [FromForm] string? linkUrl     = null,
        [FromForm] int? categoryId     = null,
        [FromForm] int? productId      = null,
        [FromForm] int? vendorId       = null,
        [FromForm] int sortOrder       = 0,
        [FromForm] bool isActive       = true,
        IFormFile? image               = null)
    {
        if (!await HasPermissionAsync("roadblocks", requireWrite: true)) return AdminForbidden("roadblocks");

        if (image == null || image.Length == 0)
            return BadRequest(new { success = false, message = "image is required." });

        var (resolvedLinkType, linkError) = await ResolveLinkAsync(linkType, linkUrl, categoryId, productId, vendorId);
        if (linkError != null) return BadRequest(new { success = false, message = linkError });

        var imageUrl = await UploadRoadblockImageAsync(image, sortOrder);

        var popup = new RoadblockPopup
        {
            ImageUrl   = imageUrl,
            LinkType   = resolvedLinkType,
            LinkUrl    = resolvedLinkType == "url" ? linkUrl : null,
            CategoryId = resolvedLinkType == "category" ? categoryId : null,
            ProductId  = resolvedLinkType == "product" ? productId : null,
            VendorId   = resolvedLinkType == "vendor" ? vendorId : null,
            SortOrder  = sortOrder,
            IsActive   = isActive,
        };
        _db.RoadblockPopups.Add(popup);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = popup.Id },
            new { success = true, message = "Roadblock popup created.", data = await ToDtoAsync(popup) });
    }

    // ─── Update ───────────────────────────────────────────────────────────

    /// <summary>Update a roadblock popup's target or image</summary>
    /// <remarks>
    /// Updates an existing popup. Send as **multipart/form-data**.
    /// Only include fields you want to change — omitted fields keep their current values.
    /// To replace the image, attach a new file; leave the `image` field empty to keep the current image.
    /// </remarks>
    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Update(
        int id,
        [FromForm] string? linkType    = null,
        [FromForm] string? linkUrl     = null,
        [FromForm] int? categoryId     = null,
        [FromForm] int? productId      = null,
        [FromForm] int? vendorId       = null,
        [FromForm] int? sortOrder      = null,
        [FromForm] bool? isActive      = null,
        IFormFile? image               = null)
    {
        if (!await HasPermissionAsync("roadblocks", requireWrite: true)) return AdminForbidden("roadblocks");

        var popup = await _db.RoadblockPopups.FindAsync(id);
        if (popup == null) return NotFound(new { success = false, message = "Roadblock popup not found." });

        if (sortOrder.HasValue) popup.SortOrder = sortOrder.Value;
        if (isActive.HasValue) popup.IsActive = isActive.Value;

        if (linkType != null)
        {
            var (resolvedLinkType, linkError) = await ResolveLinkAsync(linkType, linkUrl, categoryId, productId, vendorId);
            if (linkError != null) return BadRequest(new { success = false, message = linkError });

            popup.LinkType   = resolvedLinkType;
            popup.LinkUrl    = resolvedLinkType == "url" ? linkUrl : null;
            popup.CategoryId = resolvedLinkType == "category" ? categoryId : null;
            popup.ProductId  = resolvedLinkType == "product" ? productId : null;
            popup.VendorId   = resolvedLinkType == "vendor" ? vendorId : null;
        }

        if (image != null && image.Length > 0)
            popup.ImageUrl = await UploadRoadblockImageAsync(image, popup.SortOrder);

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Roadblock popup updated.", data = await ToDtoAsync(popup) });
    }

    // ─── Delete ───────────────────────────────────────────────────────────

    /// <summary>Delete a roadblock popup permanently</summary>
    /// <remarks>⚠️ This cannot be undone. The image in Firebase Storage is not deleted, only the database record.</remarks>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync("roadblocks", requireWrite: true)) return AdminForbidden("roadblocks");
        var popup = await _db.RoadblockPopups.FindAsync(id);
        if (popup == null) return NotFound(new { success = false, message = "Roadblock popup not found." });
        _db.RoadblockPopups.Remove(popup);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Roadblock popup deleted." });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private async Task<(string LinkType, string? Error)> ResolveLinkAsync(
        string? linkType, string? linkUrl, int? categoryId, int? productId, int? vendorId)
    {
        var resolved = string.IsNullOrWhiteSpace(linkType) ? "none" : linkType;

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

    /// <summary>Adds a human-readable category/product/vendor name for the admin UI's
    /// selected-link display — the raw entity only carries ids.</summary>
    private async Task<object> ToDtoAsync(RoadblockPopup p)
    {
        string? categoryName = null;
        string? productName  = null;
        string? vendorName   = null;

        if (p.CategoryId.HasValue)
            categoryName = await _db.Categories
                .Where(c => c.Id == p.CategoryId.Value)
                .Select(c => c.Translations.FirstOrDefault(t => t.Locale == "en") != null
                    ? c.Translations.First(t => t.Locale == "en").Name
                    : c.Translations.FirstOrDefault()!.Name)
                .FirstOrDefaultAsync();

        if (p.ProductId.HasValue)
            productName = await _db.ProductFlats
                .Where(f => f.ProductId == p.ProductId.Value)
                .OrderByDescending(f => f.Locale == "en")
                .Select(f => f.Name)
                .FirstOrDefaultAsync();

        if (p.VendorId.HasValue)
            vendorName = await _db.Vendors
                .Where(v => v.Id == p.VendorId.Value)
                .Select(v => v.Name)
                .FirstOrDefaultAsync();

        return new
        {
            p.Id,
            p.ImageUrl,
            LinkType = p.LinkType ?? "none",
            p.LinkUrl,
            p.CategoryId,
            CategoryName = categoryName,
            p.ProductId,
            ProductName = productName,
            p.VendorId,
            VendorName = vendorName,
            p.SortOrder,
            p.IsActive,
        };
    }

    private async Task<string> UploadRoadblockImageAsync(IFormFile file, int sortOrder)
    {
        var ext = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".jpg";
        var storagePath = $"roadblocks/{sortOrder}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        await using var stream = file.OpenReadStream();
        var (url, err) = await _storage.UploadFromStreamAsync(stream, storagePath, file.ContentType);
        if (err != null) throw new Exception($"Image upload failed: {err}");
        return url!;
    }
}
