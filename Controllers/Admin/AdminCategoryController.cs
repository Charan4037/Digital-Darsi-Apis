using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

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
    private readonly DOSDbContext _db;
    private readonly FirebaseStorageService _storage;

    public AdminCategoryController(
        DOSDbContext db,
        FirebaseStorageService storage,
        IConfiguration config) : base(config)
    {
        _db = db;
        _storage = storage;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all categories</summary>
    /// <remarks>
    /// Returns every category in the system as a flat list ordered by position.
    /// Use this to populate dropdowns, category pickers, or the admin category tree.
    ///
    /// **Common use cases:**
    /// - Show all categories in the admin site sidebar
    /// - Pick a parent category when creating a sub-category
    /// - Check which categories exist before creating a new one
    /// </remarks>
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

    /// <summary>Get a single category with its children</summary>
    /// <remarks>
    /// Returns full details for one category, including all its direct child categories.
    ///
    /// **When to use:** When you open a category in the admin editor and need to see its details and sub-categories.
    /// </remarks>
    /// <param name="id">The numeric ID of the category (from the List endpoint)</param>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var cat = await _db.Categories
            .AsSplitQuery()
            .Include(c => c.Translations)
            .Include(c => c.Children).ThenInclude(ch => ch.Translations)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cat == null) return NotFound(new { success = false, message = "Category not found." });
        return Ok(new { success = true, data = FormatCategory(cat, includeChildren: true) });
    }

    // ─── Create ───────────────────────────────────────────────────────────

    /// <summary>Create a new category</summary>
    /// <remarks>
    /// Creates a category and optionally uploads a logo and banner image to Firebase Storage.
    /// Send the request as **multipart/form-data** (not JSON) so you can attach image files.
    ///
    /// **Creating a top-level store category (e.g. "FoodStore", "BuildStore"):**
    /// Leave `parentId` empty — it will be created as a root category.
    ///
    /// **Creating a sub-category (e.g. "Vegetables" inside "FoodStore"):**
    /// Set `parentId` to the ID of the parent category.
    ///
    /// **Field guide:**
    /// - `name` — The display name shown in the app (e.g. "Fresh Vegetables")
    /// - `parentId` — ID of the parent category. Leave blank for a top-level category
    /// - `slug` — URL-friendly key (e.g. "fresh-vegetables"). Auto-generated from name if left blank
    /// - `position` — Sort order. Lower number = appears first. Default is 0
    /// - `status` — true = visible in the app, false = hidden. Default is true
    /// - `logo` — Category icon/logo image file (jpg, png, webp, gif)
    /// - `banner` — Wide banner image shown at the top of the category page
    /// </remarks>
    /// <param name="name">Display name of the category (required)</param>
    /// <param name="parentId">ID of the parent category. Omit to create a root/top-level category</param>
    /// <param name="slug">URL slug — auto-generated from name if not provided</param>
    /// <param name="position">Sort order (0 = first). Default: 0</param>
    /// <param name="status">true = visible in app, false = hidden. Default: true</param>
    /// <param name="logo">Logo/icon image file (jpg, png, webp, gif)</param>
    /// <param name="banner">Banner image file shown at top of category page</param>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create(
        [FromForm] string name,
        [FromForm] int? parentId,
        [FromForm] string? slug,
        [FromForm] int position = 0,
        [FromForm] bool status = true,
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
            Locale           = "en",
        };
        _db.Set<CategoryTranslation>().Add(translation);
        await _db.SaveChangesAsync();

        category.Translations.Add(translation);
        return CreatedAtAction(nameof(Get), new { id = category.Id },
            new { success = true, message = "Category created.", data = FormatCategory(category) });
    }

    // ─── Update ───────────────────────────────────────────────────────────

    /// <summary>Update an existing category</summary>
    /// <remarks>
    /// Updates a category's details. Send as **multipart/form-data**.
    /// Only include the fields you want to change — omitted fields stay unchanged.
    /// To replace the logo or banner, attach a new image file; omit the field to keep the current one.
    ///
    /// **Example:** To just rename a category, send only `name`. Everything else stays the same.
    /// </remarks>
    /// <param name="id">ID of the category to update</param>
    /// <param name="name">New display name (leave blank to keep current)</param>
    /// <param name="parentId">Move to a different parent category (leave blank to keep current)</param>
    /// <param name="slug">New URL slug (leave blank to keep current)</param>
    /// <param name="position">New sort order position</param>
    /// <param name="status">true = visible, false = hidden</param>
    /// <param name="logo">New logo image file (replaces existing)</param>
    /// <param name="banner">New banner image file (replaces existing)</param>
    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Update(
        int id,
        [FromForm] string? name,
        [FromForm] int? parentId,
        [FromForm] string? slug,
        [FromForm] int? position,
        [FromForm] bool? status,
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
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Category updated.", data = FormatCategory(cat) });
    }

    // ─── Toggle status ────────────────────────────────────────────────────

    /// <summary>Toggle a category's visibility (active/hidden)</summary>
    /// <remarks>
    /// Flips the category's status between active (visible in app) and hidden.
    /// No request body needed — just call the endpoint and it toggles automatically.
    ///
    /// **Use this to:** Temporarily hide a category without deleting it.
    /// </remarks>
    /// <param name="id">ID of the category to toggle</param>
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

    /// <summary>Delete a category permanently</summary>
    /// <remarks>
    /// Permanently deletes a category from the system.
    ///
    /// **What happens to child categories?** They are automatically re-parented to the deleted category's parent.
    /// So if you delete "Fresh Produce" (which is under "FoodStore"), its children like "Vegetables" and "Fruits"
    /// move up to be directly under "FoodStore".
    ///
    /// **What happens to products?** Products that were assigned to this category keep their other category
    /// assignments. They are NOT deleted.
    ///
    /// ⚠️ This action cannot be undone. Use toggle-status to hide a category instead if you might need it again.
    /// </remarks>
    /// <param name="id">ID of the category to delete</param>
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
