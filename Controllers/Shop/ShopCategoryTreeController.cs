using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/category-trees")]
[Tags("CategoryTree")]
[AllowAnonymous]
public class ShopCategoryTreeController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly string _locale;

    public ShopCategoryTreeController(BagistoDbContext db, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
    }

    /// <summary>Get recursive category tree</summary>
    [HttpGet]
    public async Task<IActionResult> GetCategoryTree()
    {
        var all = await _db.Categories
            .Include(c => c.Translations)
            .Where(c => c.Status)
            .OrderBy(c => c.Position)
            .ToListAsync();

        var roots = all.Where(c => c.ParentId == null || c.ParentId == 0).ToList();

        object BuildTree(Models.Catalog.Category cat)
        {
            var t = cat.Translations.FirstOrDefault(t => t.Locale == _locale)
                 ?? cat.Translations.FirstOrDefault();
            var children = all.Where(c => c.ParentId == cat.Id).ToList();

            return new
            {
                cat.Id,
                cat.ParentId,
                Name = t?.Name,
                Slug = t?.Slug,
                Url = t?.UrlPath,
                Status = cat.Status ? 1 : 0,
                Children = children.Select(BuildTree).ToList()
            };
        }

        return Ok(roots.Select(BuildTree).ToList());
    }
}
