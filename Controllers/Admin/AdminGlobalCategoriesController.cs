using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin global categories controller.
/// Routes: /api/v1/admin/global-categories
/// </summary>
[Route("api/v1/admin/global-categories")]
[Tags("Admin – Global Categories")]
public class AdminGlobalCategoriesController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly FirebaseStorageService _storage;
    private readonly string _baseUrl;

    // Root category id in this dataset — every real category lives under it.
    // Treated as the implicit "top level" parent and never itself editable/deletable.
    // Internal (not private): AdminVendorsController.GetCategories mirrors this
    // controller's hierarchy mapping and needs the same constant.
    internal const int RootCategoryId = 1;
    private const int ReparentOffset = 1_000_000;

    public AdminGlobalCategoriesController(DOSDbContext db, FirebaseStorageService storage, IConfiguration config) : base(config)
    {
        _db = db;
        _storage = storage;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    // Older categories carry a legacy Bagisto-relative logo/banner path
    // (e.g. "category/3/cat_3.jpg"); newer ones store a full Firebase URL.
    // Mirrors CategoryController's storefront-facing ResolveAssetUrl.
    private string? ResolveAssetUrl(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http://") || path.StartsWith("https://")) return path;
        return $"{_baseUrl}/storage/{path}";
    }

    /// <summary>List all categories (flat list, tree pre-order — parents always precede their descendants)</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var categories = await _db.Categories
            .Include(c => c.Translations)
            .AsNoTracking()
            .Where(c => c.Id != RootCategoryId)
            .OrderBy(c => c.Lft)
            .ToListAsync();

        // Product counts via a separate GROUP BY instead of .Include(c => c.Products)
        // — that Include cartesian-joined every category row against every one of
        // its products just to read a count, which is fine on a fast local DB but
        // took 30+ seconds (then failed) against the real prod DB's latency.
        var productCounts = await _db.Products
            .Where(p => p.ParentId == null)
            .SelectMany(p => p.Categories.Select(c => c.Id))
            .GroupBy(id => id)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        var namesById = categories.ToDictionary(c => c.Id, c => c.Translations.FirstOrDefault()?.Name ?? "");

        var results = categories.Select(c => new AdminCategoryDto
        {
            Id = c.Id,
            Name = c.Translations.FirstOrDefault()?.Name ?? "",
            Slug = c.Translations.FirstOrDefault()?.Slug ?? "",
            Description = c.Translations.FirstOrDefault()?.Description ?? "",
            Active = c.Status,
            VendorCount = 1, // TODO: Calculate vendor count
            ProductCount = productCounts.GetValueOrDefault(c.Id, 0),
            ParentId = c.ParentId == RootCategoryId ? null : c.ParentId,
            ParentName = (c.ParentId.HasValue && c.ParentId != RootCategoryId && namesById.TryGetValue(c.ParentId.Value, out var pn)) ? pn : null,
            LogoUrl = ResolveAssetUrl(c.LogoPath),
            BannerUrl = ResolveAssetUrl(c.BannerPath),
            NameTe = c.Translations.FirstOrDefault(t => t.Locale == "te")?.Name,
            DescriptionTe = c.Translations.FirstOrDefault(t => t.Locale == "te")?.Description
        }).ToList();

        return Ok(new CategoryListResponse { Data = results });
    }

    /// <summary>Create a new category. Omit parentId (or send null) for a top-level category.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "name is required" });
        if (string.IsNullOrWhiteSpace(request.Slug))
            return BadRequest(new { message = "slug is required" });

        var slug = Slugify(request.Slug);

        if (await _db.CategoryTranslations.AnyAsync(t => t.Slug == slug))
            return Conflict(new { message = "A category with this slug already exists" });

        var parentId = request.ParentId ?? RootCategoryId;
        var parent = await _db.Categories.FindAsync(parentId);
        if (parent == null)
            return BadRequest(new { message = $"Parent category {parentId} not found" });

        // Nested-set insertion: make room as the last child of parent.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft + 2 WHERE _lft >= {0}", parent.Rgt);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _rgt = _rgt + 2 WHERE _rgt >= {0}", parent.Rgt);

        var category = new Category
        {
            Status = request.Active,
            Position = 0,
            ParentId = parentId,
            Lft = parent.Rgt,
            Rgt = parent.Rgt + 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        var translation = new CategoryTranslation
        {
            CategoryId = category.Id,
            Name = request.Name,
            Slug = slug,
            Description = request.Description ?? "",
            Locale = "en"
        };

        _db.CategoryTranslations.Add(translation);

        // Telugu translation — same slug as the English row (confirmed safe:
        // real scraped categories already share one slug across both locale
        // rows). Falls back to the English text when the admin leaves the
        // Telugu tab blank.
        var nameTe = !string.IsNullOrWhiteSpace(request.NameTe) ? request.NameTe!.Trim() : request.Name;
        var descTe = !string.IsNullOrWhiteSpace(request.DescriptionTe) ? request.DescriptionTe : (request.Description ?? "");
        _db.CategoryTranslations.Add(new CategoryTranslation
        {
            CategoryId = category.Id,
            Name = nameTe,
            Slug = slug,
            Description = descTe,
            Locale = "te"
        });

        await _db.SaveChangesAsync();

        return Ok(new CreatedResponse<AdminCategoryDto>
        {
            Data = new AdminCategoryDto
            {
                Id = category.Id,
                Name = request.Name,
                Slug = slug,
                Description = request.Description ?? "",
                Active = request.Active,
                VendorCount = 0,
                ProductCount = 0,
                ParentId = parentId == RootCategoryId ? null : parentId,
                NameTe = nameTe,
                DescriptionTe = descTe
            },
            Message = "Category added"
        });
    }

    /// <summary>Update a category, optionally moving it under a new parent</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCategoryRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (id == RootCategoryId) return BadRequest(new { message = "The root category cannot be edited" });

        var category = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
            return NotFound(new { message = "Category not found" });

        var slug = Slugify(request.Slug);

        if (await _db.CategoryTranslations.AnyAsync(t => t.Slug == slug && t.CategoryId != id))
            return Conflict(new { message = "A category with this slug already exists" });

        var newParentId = request.ParentId ?? RootCategoryId;
        if (newParentId != category.ParentId)
        {
            var moveError = await MoveSubtreeAsync(category, newParentId);
            if (moveError != null) return BadRequest(new { message = moveError });
        }

        category.Status = request.Active;
        category.UpdatedAt = DateTime.UtcNow;

        var translation = category.Translations.FirstOrDefault(t => t.Locale == "en")
            ?? category.Translations.FirstOrDefault();

        if (translation != null)
        {
            translation.Name = request.Name;
            translation.Slug = slug;
            translation.Description = request.Description ?? "";
        }

        // Telugu translation — find or create so every edit keeps it in sync
        // instead of leaving it stale (previously only the English row was
        // ever touched, so an edited scraped category's Telugu name/slug
        // would silently diverge from the new English content). Text fields
        // fall back to the English value when the admin leaves the Telugu
        // tab blank.
        var teTranslation = category.Translations.FirstOrDefault(t => t.Locale == "te");
        var nameTe = !string.IsNullOrWhiteSpace(request.NameTe) ? request.NameTe!.Trim() : request.Name;
        var descTe = !string.IsNullOrWhiteSpace(request.DescriptionTe)
            ? request.DescriptionTe
            : (request.Description ?? teTranslation?.Description ?? "");
        if (teTranslation != null)
        {
            teTranslation.Name = nameTe;
            teTranslation.Slug = slug;
            teTranslation.Description = descTe;
        }
        else
        {
            _db.CategoryTranslations.Add(new CategoryTranslation
            {
                CategoryId = category.Id,
                Name = nameTe,
                Slug = slug,
                Description = descTe,
                Locale = "te"
            });
        }

        await _db.SaveChangesAsync();

        return Ok(new UpdatedResponse<AdminCategoryDto>
        {
            Data = new AdminCategoryDto
            {
                Id = category.Id,
                Name = request.Name,
                Slug = slug,
                Description = request.Description ?? "",
                Active = request.Active,
                VendorCount = 1, // TODO: Calculate
                ProductCount = category.Products.Count,
                ParentId = category.ParentId == RootCategoryId ? null : category.ParentId,
                LogoUrl = ResolveAssetUrl(category.LogoPath),
                BannerUrl = ResolveAssetUrl(category.BannerPath),
                NameTe = nameTe,
                DescriptionTe = descTe
            },
            Message = "Category updated"
        });
    }

    /// <summary>
    /// Moves a category (and its whole subtree) to be the last child of newParentId,
    /// rebalancing the nested-set _lft/_rgt columns so subtree/product-count queries
    /// elsewhere in the app stay correct.
    /// </summary>
    private async Task<string?> MoveSubtreeAsync(Category category, int newParentId)
    {
        if (newParentId == category.Id)
            return "A category cannot be its own parent";

        var newParent = await _db.Categories.FindAsync(newParentId);
        if (newParent == null)
            return $"Parent category {newParentId} not found";

        if (newParent.Lft >= category.Lft && newParent.Rgt <= category.Rgt)
            return "Cannot move a category under one of its own sub-categories";

        var catLft = category.Lft;
        var catRgt = category.Rgt;
        var width = catRgt - catLft + 1;

        // 1. Lift the subtree out of the tree order entirely (tag with a large offset).
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft + {0}, _rgt = _rgt + {0} WHERE _lft >= {1} AND _rgt <= {2}",
            ReparentOffset, catLft, catRgt);

        // 2. Close the gap left behind at the old location.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft - {0} WHERE _lft > {1} AND _lft < {2}", width, catRgt, ReparentOffset);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _rgt = _rgt - {0} WHERE _rgt > {1} AND _rgt < {2}", width, catRgt, ReparentOffset);

        // 3. Re-read the new parent's rgt — it may have shifted in step 2.
        await _db.Entry(newParent).ReloadAsync();
        var insertionPoint = newParent.Rgt;

        // 4. Open a gap as the new parent's last child.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft + {0} WHERE _lft >= {1} AND _lft < {2}", width, insertionPoint, ReparentOffset);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _rgt = _rgt + {0} WHERE _rgt >= {1} AND _rgt < {2}", width, insertionPoint, ReparentOffset);

        // 5. Drop the subtree back in at the new position.
        var delta = insertionPoint - catLft - ReparentOffset;
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft + {0}, _rgt = _rgt + {0} WHERE _lft >= {1}", delta, ReparentOffset + catLft);

        // Lft/Rgt on `category` are now stale in memory (changed via raw SQL above) — refresh
        // so any later read in this request sees the real values, then reapply the parent change
        // (ReloadAsync would otherwise overwrite it with the old, pre-move parent id).
        await _db.Entry(category).ReloadAsync();
        category.ParentId = newParentId;

        return null;
    }

    /// <summary>Update category status (activate/deactivate)</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });
        if (id == RootCategoryId) return BadRequest(new { message = "The root category cannot be edited" });

        var category = await _db.Categories.FindAsync(id);
        if (category == null)
            return NotFound(new { message = "Category not found" });

        category.Status = request.Active.Value;
        await _db.SaveChangesAsync();

        var status = request.Active.Value ? "Active" : "Inactive";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = category.Id, active = request.Active.Value },
            Message = $"Category is now {status}"
        });
    }

    /// <summary>Upload/replace a category's logo image. Max 1MB — a small icon, not a photo.</summary>
    [HttpPost("{id:int}/logo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(1_000_000)]
    public Task<IActionResult> UploadLogo(int id, IFormFile file) => UploadImageAsync(id, "logo", file);

    /// <summary>Upload/replace a category's banner image. Max 2MB.</summary>
    [HttpPost("{id:int}/banner")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_000_000)]
    public Task<IActionResult> UploadBanner(int id, IFormFile file) => UploadImageAsync(id, "banner", file);

    /// <summary>Remove a category's logo image</summary>
    [HttpDelete("{id:int}/logo")]
    public Task<IActionResult> DeleteLogo(int id) => DeleteImageAsync(id, "logo");

    /// <summary>Remove a category's banner image</summary>
    [HttpDelete("{id:int}/banner")]
    public Task<IActionResult> DeleteBanner(int id) => DeleteImageAsync(id, "banner");

    private async Task<IActionResult> UploadImageAsync(int id, string kind, IFormFile file)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (id == RootCategoryId) return BadRequest(new { message = "The root category cannot be edited" });
        if (file == null || file.Length == 0) return BadRequest(new { message = "file is required" });

        var category = await _db.Categories.Include(c => c.Translations).FirstOrDefaultAsync(c => c.Id == id);
        if (category == null) return NotFound(new { message = "Category not found" });

        var slug = category.Translations.FirstOrDefault(t => t.Locale == "en")?.Slug
            ?? category.Translations.FirstOrDefault()?.Slug
            ?? $"cat-{id}";

        var ext = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".jpg";
        var storagePath = $"categories/{slug}-{kind}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}";

        await using var stream = file.OpenReadStream();
        var (url, err) = await _storage.UploadFromStreamAsync(stream, storagePath, file.ContentType);
        if (err != null) return StatusCode(500, new { message = $"Image upload failed: {err}" });

        if (kind == "logo") category.LogoPath = url; else category.BannerPath = url;
        category.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = $"{kind} updated", url });
    }

    private async Task<IActionResult> DeleteImageAsync(int id, string kind)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (id == RootCategoryId) return BadRequest(new { message = "The root category cannot be edited" });

        var category = await _db.Categories.FindAsync(id);
        if (category == null) return NotFound(new { message = "Category not found" });

        if (kind == "logo") category.LogoPath = null; else category.BannerPath = null;
        category.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = $"{kind} removed" });
    }

    /// <summary>Delete a category. Its direct children (if any) are re-parented to its own parent.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (id == RootCategoryId) return BadRequest(new { message = "The root category cannot be deleted" });

        var category = await _db.Categories
            .Include(c => c.Products)
            .Include(c => c.Children)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
            return NotFound(new { message = "Category not found" });

        if (category.Products.Count > 0)
            return Conflict(new { message = $"Cannot delete – {category.Products.Count} products in this category" });

        foreach (var child in category.Children)
        {
            var err = await MoveSubtreeAsync(child, category.ParentId ?? RootCategoryId);
            if (err != null) return BadRequest(new { message = err });
        }

        // Re-fetch: MoveSubtreeAsync above may have shifted this category's own _lft/_rgt.
        await _db.Entry(category).ReloadAsync();

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();

        var width = category.Rgt - category.Lft + 1;
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _lft = _lft - {0} WHERE _lft > {1}", width, category.Rgt);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE categories SET _rgt = _rgt - {0} WHERE _rgt > {1}", width, category.Rgt);

        return Ok(new { message = "Category deleted" });
    }
}
