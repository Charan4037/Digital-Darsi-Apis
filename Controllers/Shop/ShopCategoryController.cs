using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/categories")]
[Tags("Category")]
[AllowAnonymous]
public class ShopCategoryController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly string _locale;
    private readonly string _baseUrl;

    public ShopCategoryController(BagistoDbContext db, IConfiguration config, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
        _baseUrl = (config["App:BaseUrl"] ?? "").TrimEnd('/');
    }

    [HttpGet]
    public async Task<IActionResult> GetCategories()
    {
        var categories = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Children).ThenInclude(ch => ch.Translations)
            .Where(c => c.ParentId == null)
            .OrderBy(c => c.Position)
            .ToListAsync();

        return Ok(categories.Select(c => FormatCategory(c, true)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCategory(int id)
    {
        var c = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Children).ThenInclude(ch => ch.Translations)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return NotFound(new { message = "Category not found." });

        return Ok(FormatCategory(c, true));
    }

    private object FormatCategory(Models.Catalog.Category c, bool includeChildren)
    {
        var t = c.Translations.FirstOrDefault(t => t.Locale == _locale)
             ?? c.Translations.FirstOrDefault();

        var slug = t?.Slug ?? "";
        var result = new Dictionary<string, object?>
        {
            ["id"] = c.Id,
            ["position"] = c.Position,
        };

        if (c.LogoPath != null)
            result["logoPath"] = c.LogoPath;

        result["status"] = c.Status ? 1 : 0;
        result["displayMode"] = c.DisplayMode ?? "products_and_description";
        result["_lft"] = c.Lft;
        result["_rgt"] = c.Rgt;
        result["createdAt"] = c.CreatedAt;
        result["updatedAt"] = c.UpdatedAt;
        result["url"] = $"{_baseUrl}/{slug}";

        if (c.LogoPath != null)
            result["logoUrl"] = $"{_baseUrl}/storage/{c.LogoPath}";

        result["translation"] = t != null ? new
        {
            t.Id,
            t.CategoryId,
            t.Name,
            t.Slug,
            t.UrlPath,
            t.Description,
            t.MetaTitle,
            t.MetaDescription,
            t.MetaKeywords,
            t.Locale
        } : null;

        result["translations"] = c.Translations
            .Select(tr => $"/api/shop/category_translations/{tr.Id}")
            .ToList();

        if (c.ParentId != null)
            result["parent"] = $"/api/shop/categories/{c.ParentId}";

        result["children"] = includeChildren
            ? c.Children.OrderBy(ch => ch.Position).Select(ch => FormatCategory(ch, false)).ToList()
            : new List<object>();

        return result;
    }
}
