using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;

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

    public AdminGlobalCategoriesController(DOSDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    /// <summary>List all categories</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var categories = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Products)
            .AsNoTracking()
            .OrderBy(c => c.Position)
            .ToListAsync();

        var results = categories.Select(c => new AdminCategoryDto
        {
            Id = c.Id,
            Name = c.Translations.FirstOrDefault()?.Name ?? "",
            Slug = c.Translations.FirstOrDefault()?.Slug ?? "",
            Description = c.Translations.FirstOrDefault()?.Description ?? "",
            Active = c.Status,
            VendorCount = 1, // TODO: Calculate vendor count
            ProductCount = c.Products.Count
        }).ToList();

        return Ok(new CategoryListResponse { Data = results });
    }

    /// <summary>Create a new category</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "name is required" });
        if (string.IsNullOrWhiteSpace(request.Slug))
            return BadRequest(new { message = "slug is required" });

        var slug = Slugify(request.Slug);

        // Check slug uniqueness
        if (await _db.CategoryTranslations.AnyAsync(t => t.Slug == slug))
            return Conflict(new { message = "A category with this slug already exists" });

        var category = new Models.Catalog.Category
        {
            Status = request.Active,
            Position = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        var translation = new Models.Catalog.CategoryTranslation
        {
            CategoryId = category.Id,
            Name = request.Name,
            Slug = slug,
            Description = request.Description ?? "",
            Locale = "en"
        };

        _db.CategoryTranslations.Add(translation);
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
                ProductCount = 0
            },
            Message = "Category added"
        });
    }

    /// <summary>Update a category</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCategoryRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var category = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
            return NotFound(new { message = "Category not found" });

        var slug = Slugify(request.Slug);

        // Check slug uniqueness
        if (await _db.CategoryTranslations.AnyAsync(t => t.Slug == slug && t.CategoryId != id))
            return Conflict(new { message = "A category with this slug already exists" });

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
                ProductCount = category.Products.Count
            },
            Message = "Category updated"
        });
    }

    /// <summary>Update category status (activate/deactivate)</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

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

    /// <summary>Delete a category</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var category = await _db.Categories
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category == null)
            return NotFound(new { message = "Category not found" });

        if (category.Products.Count > 0)
            return Conflict(new { message = $"Cannot delete – {category.Products.Count} products in this category" });

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Category deleted" });
    }
}
