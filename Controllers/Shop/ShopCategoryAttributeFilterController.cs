using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/filters")]
[Tags("CategoryAttributeFilter")]
[AllowAnonymous]
public class ShopCategoryAttributeFilterController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly string _locale;

    public ShopCategoryAttributeFilterController(BagistoDbContext db, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
    }

    /// <summary>Get filterable attributes</summary>
    [HttpGet("attributes")]
    public async Task<IActionResult> GetFilterableAttributes([FromQuery] int? categoryId)
    {
        if (categoryId.HasValue)
        {
            var category = await _db.Categories
                .Include(c => c.FilterableAttributes).ThenInclude(a => a.Translations)
                .FirstOrDefaultAsync(c => c.Id == categoryId);

            if (category == null)
                return NotFound(new { message = "Category not found." });

            return Ok(category.FilterableAttributes.Select(FormatAttribute).ToList());
        }
        else
        {
            var attrs = await _db.Attributes
                .Include(a => a.Translations)
                .Where(a => a.IsFilterable)
                .ToListAsync();

            return Ok(attrs.Select(FormatAttribute).ToList());
        }
    }

    private object FormatAttribute(Models.Catalog.Attribute a)
    {
        var name = a.Translations.FirstOrDefault(t => t.Locale == _locale)?.Name ?? a.AdminName;

        return new
        {
            a.Id,
            a.Code,
            a.Type,
            Name = name
        };
    }
}
