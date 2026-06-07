using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for categories.
/// Routes: GET/POST/PUT/DELETE /api/v1/admin/categories
///
/// Images (logo + banner) are accepted as multipart/form-data IFormFile
/// fields and uploaded directly to Firebase Storage.
/// </summary>
[Route("api/v1/admin/categories")]
[Tags("Admin – Categories")]
public class AdminCategoryController : AdminBaseController
{
    private readonly BagistoDbContext _db;
    private readonly FirebaseStorageService _storage;

    public AdminCategoryController(
        BagistoDbContext db,
        FirebaseStorageService storage,
        IConfiguration config) : base(config)
    {
        _db = db;
        _storage = storage;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all categories (flat, ordered by position).</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var cats = await _db.Categories
            .Include(c => c.Translations)
            .OrderBy(c => c.Position)
            .AsNoTracking()
            .ToListAsync();

        var result = cats.Select(c => FormatCategory(c)).ToList();
        return Ok(new { success = true, data = result });
    }

    // ─── Get single ───────────────────────────────────────────────────────

    /// <summary>Get one category by ID with its children.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var cat = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Children).ThenInclude(ch => ch.Translations)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cat == null) return NotFound(new { success = false, message = "Category not found." });
        return Ok(new { success = true, data = FormatCategory(cat, includeChildren: true) });
    }

    // ─── Create ───────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new category.
    /// Accept multipart/form-data with optional logo and banner images.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create(
        [FromForm] string name,
        [FromForm] int? parentId,
        [FromForm] string? slug,
        [FromForm] string? description,
        [FromForm] int position = 0,
        [FromForm] bool status = true,
        [FromForm] string? metaTitle = null,
        [FromForm] string? metaDescription = null,
        [FromForm] string? metaKeywords = null,
        IFormFile? logo = null,
        IFormFile? banner = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { success = false, message = "name is required." });

        var resolvedSlug = string.IsNullOrWhiteSpace(slug) ? Slugify(name) : Slugify(slug);

        // Ensure slug is unique by appending timestamp suffix when needed
        var slugExists = await _db.Set<CategoryTranslation>()
            .AnyAsync(t => t.Slug == resolvedSlug);
        if (slugExists) resolvedSlug = $"{resolvedSlug}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // ── Nested-set insertion ─────────────────────────────────────────
        int newLft, newRgt;
        if (parentId.HasValue)
        {
            var parent = await _db.Categories.FindAsync(parentId.Value);
            if (parent == null)
                return BadRequest(new { success = false, message = $"Parent category {parentId} not found." });

            // Shift all nodes to make room for the new leaf inside parent
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE categories SET _lft = _lft + 2 WHERE _lft >= {0}", parent.Rgt);
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE categories SET _rgt = _rgt + 2 WHERE _rgt >= {0}", parent.Rgt);

            newLft = parent.Rgt;
            newRgt = parent.Rgt + 1;
        }
        else
        {
            // Root-level: append after the highest rgt
            var maxRgt = await _db.Categories.MaxAsync(c => (int?)c.Rgt) ?? 0;
            newLft = maxRgt + 1;
            newRgt = maxRgt + 2;
        }

        // ── Image uploads ────────────────────────────────────────────────
        string? logoUrl   = await UploadCategoryImageAsync(logo,   "logo",   resolvedSlug);
        string? bannerUrl = await UploadCategoryImageAsync(banner, "banner", resolvedSlug);

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Position    = position,
            Status      = status,
            ParentId    = parentId,
            Lft         = newLft,
            Rgt         = newRgt,
            LogoPath    = logoUrl,
            BannerPath  = bannerUrl,
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        var urlPath = parentId.HasValue
            ? $"{resolvedSlug}"
            : resolvedSlug;

        var translation = new CategoryTranslation
        {
            CategoryId       = category.Id,
            Name             = name,
            Slug             = resolvedSlug,
            UrlPath          = urlPath,
            Description      = description,
            MetaTitle        = metaTitle,
            MetaDescription  = metaDescription,
            MetaKeywords     = metaKeywords,
            Locale           = "en",
        };
        _db.Set<CategoryTranslation>().Add(translation);
        await _db.SaveChangesAsync();

        category.Translations.Add(translation);
        return CreatedAtAction(nameof(Get), new { id = category.Id },
            new { success = true, message = "Category created.", data = FormatCategory(category) });
    }

    // ─── Update ───────────────────────────────────────────────────────────

    /// <summary>
    /// Update an existing category.
    /// Pass only the fields you want to change.
    /// To replace an image send the new file; omit the field to keep the current image.
    /// </summary>
    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Update(
        int id,
        [FromForm] string? name,
        [FromForm] int? parentId,
        [FromForm] string? slug,
        [FromForm] string? description,
        [FromForm] int? position,
        [FromForm] bool? status,
        [FromForm] string? metaTitle,
        [FromForm] string? metaDescription,
        [FromForm] string? metaKeywords,
        IFormFile? logo = null,
        IFormFile? banner = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var cat = await _db.Categories
            .Include(c => c.Translations)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cat == null) return NotFound(new { success = false, message = "Category not found." });

        var tr = cat.Translations.FirstOrDefault(t => t.Locale == "en")
                 ?? cat.Translations.FirstOrDefault();

        if (position.HasValue) cat.Position = position.Value;
        if (status.HasValue)   cat.Status   = status.Value;
        if (parentId.HasValue) cat.ParentId  = parentId.Value;
        cat.UpdatedAt = DateTime.UtcNow;

        if (logo != null)
        {
            var slugKey = tr?.Slug ?? Slugify(tr?.Name ?? $"cat-{id}");
            cat.LogoPath = await UploadCategoryImageAsync(logo, "logo", slugKey);
        }
        if (banner != null)
        {
            var slugKey = tr?.Slug ?? Slugify(tr?.Name ?? $"cat-{id}");
            cat.BannerPath = await UploadCategoryImageAsync(banner, "banner", slugKey);
        }

        if (tr != null)
        {
            if (!string.IsNullOrWhiteSpace(name))   tr.Name = name;
            if (!string.IsNullOrWhiteSpace(slug))   tr.Slug = Slugify(slug);
            if (description  != null) tr.Description     = description;
            if (metaTitle    != null) tr.MetaTitle        = metaTitle;
            if (metaDescription != null) tr.MetaDescription = metaDescription;
            if (metaKeywords != null) tr.MetaKeywords     = metaKeywords;
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Category updated.", data = FormatCategory(cat) });
    }

    // ─── Toggle status ────────────────────────────────────────────────────

    [HttpPatch("{id:int}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var cat = await _db.Categories.FindAsync(id);
        if (cat == null) return NotFound(new { success = false, message = "Category not found." });

        cat.Status    = !cat.Status;
        cat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = $"Category status set to {cat.Status}.", status = cat.Status });
    }

    // ─── Delete ───────────────────────────────────────────────────────────

    /// <summary>
    /// Hard-delete a category. Children are re-parented to the deleted
    /// category's parent (or become root-level). Products retain their
    /// other category assignments.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var cat = await _db.Categories
            .Include(c => c.Children)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cat == null) return NotFound(new { success = false, message = "Category not found." });

        // Re-parent direct children to this category's parent
        foreach (var child in cat.Children)
            child.ParentId = cat.ParentId;

        _db.Categories.Remove(cat);
        await _db.SaveChangesAsync();

        // Rebuild nested set tree (shift lft/rgt to close the gap)
        var width = cat.Rgt - cat.Lft + 1;
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft - {0} WHERE _lft > {1}", width, cat.Rgt);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _rgt = _rgt - {0} WHERE _rgt > {1}", width, cat.Rgt);

        return Ok(new { success = true, message = "Category deleted." });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private async Task<string?> UploadCategoryImageAsync(IFormFile? file, string kind, string slug)
    {
        if (file == null || file.Length == 0) return null;
        var ext       = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".jpg";
        var storagePath = $"categories/{slug}-{kind}{ext}";
        await using var stream = file.OpenReadStream();
        var (url, err) = await _storage.UploadFromStreamAsync(stream, storagePath, file.ContentType);
        if (err != null) throw new Exception($"Image upload failed: {err}");
        return url;
    }

    private static object FormatCategory(Category c, bool includeChildren = false)
    {
        var tr = c.Translations.FirstOrDefault(t => t.Locale == "en")
                 ?? c.Translations.FirstOrDefault();
        var obj = new
        {
            id          = c.Id,
            name        = tr?.Name ?? "",
            slug        = tr?.Slug ?? "",
            description = tr?.Description,
            url_path    = tr?.UrlPath ?? "",
            parent_id   = c.ParentId,
            position    = c.Position,
            status      = c.Status,
            logo_url    = c.LogoPath,
            banner_url  = c.BannerPath,
            meta_title        = tr?.MetaTitle,
            meta_description  = tr?.MetaDescription,
            meta_keywords     = tr?.MetaKeywords,
            created_at  = c.CreatedAt,
            updated_at  = c.UpdatedAt,
            children    = includeChildren
                ? c.Children.Select(ch => FormatCategory(ch)).ToList()
                : null,
        };
        return obj;
    }
}
