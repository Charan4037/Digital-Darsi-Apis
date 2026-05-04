using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/v1/categories")]
[Tags("Categories")]
[ApiExplorerSettings(IgnoreApi = true)]
public class CategoryController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly ProductService _productService;
    private readonly string _locale;

    public CategoryController(BagistoDbContext db, ProductService productService, LocaleContext localeCtx)
    {
        _db = db;
        _productService = productService;
        _locale = localeCtx.Locale;
    }

    /// <summary>List all categories</summary>
    [HttpGet]
    public async Task<IActionResult> GetCategories([FromQuery] int? parentId)
    {
        var q = _db.Categories
            .Include(c => c.Translations)
            .Where(c => c.Status);

        if (parentId.HasValue)
            q = q.Where(c => c.ParentId == parentId);

        var categories = await q.OrderBy(c => c.Position).ToListAsync();

        var data = categories.Select(c =>
        {
            var t = c.Translations.FirstOrDefault(t => t.Locale == _locale) ?? c.Translations.FirstOrDefault();
            return new
            {
                c.Id,
                Name = t?.Name,
                Slug = t?.Slug,
                UrlPath = t?.UrlPath,
                Description = t?.Description,
                c.ParentId,
                c.Position,
                c.LogoPath,
                c.BannerPath,
                c.DisplayMode,
                MetaTitle = t?.MetaTitle,
                MetaDescription = t?.MetaDescription,
                MetaKeywords = t?.MetaKeywords
            };
        });

        return Ok(new { data });
    }

    /// <summary>Get category tree structure</summary>
    [HttpGet("tree")]
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
            var t = cat.Translations.FirstOrDefault(t => t.Locale == _locale) ?? cat.Translations.FirstOrDefault();
            var children = all.Where(c => c.ParentId == cat.Id).ToList();
            return new
            {
                cat.Id,
                Name = t?.Name,
                Slug = t?.Slug,
                UrlPath = t?.UrlPath,
                cat.Position,
                cat.LogoPath,
                cat.BannerPath,
                Children = children.Select(BuildTree)
            };
        }

        return Ok(new { data = roots.Select(BuildTree) });
    }

    /// <summary>Get filterable attributes for a category</summary>
    [HttpGet("attributes")]
    public async Task<IActionResult> GetCategoryAttributes([FromQuery] int? categoryId)
    {
        if (!categoryId.HasValue)
        {
            var attrs = await _db.Attributes
                .Include(a => a.Translations)
                .Include(a => a.Options).ThenInclude(o => o.Translations)
                .Where(a => a.IsFilterable)
                .ToListAsync();

            return Ok(new
            {
                data = attrs.Select(a => new
                {
                    a.Id,
                    a.Code,
                    a.AdminName,
                    a.Type,
                    Name = a.Translations.FirstOrDefault(t => t.Locale == _locale)?.Name ?? a.AdminName,
                    Options = a.Options.Select(o => new
                    {
                        o.Id,
                        Label = o.Translations.FirstOrDefault(t => t.Locale == _locale)?.Label ?? o.AdminName
                    })
                })
            });
        }

        var category = await _db.Categories
            .Include(c => c.FilterableAttributes).ThenInclude(a => a.Translations)
            .Include(c => c.FilterableAttributes).ThenInclude(a => a.Options).ThenInclude(o => o.Translations)
            .FirstOrDefaultAsync(c => c.Id == categoryId);

        if (category == null) return NotFound(new { message = "Category not found." });

        return Ok(new
        {
            data = category.FilterableAttributes.Select(a => new
            {
                a.Id,
                a.Code,
                a.AdminName,
                a.Type,
                Name = a.Translations.FirstOrDefault(t => t.Locale == _locale)?.Name ?? a.AdminName,
                Options = a.Options.Select(o => new
                {
                    o.Id,
                    Label = o.Translations.FirstOrDefault(t => t.Locale == _locale)?.Label ?? o.AdminName
                })
            })
        });
    }

    /// <summary>Get attribute options by attribute ID</summary>
    [HttpGet("attributes/{attributeId:int}/options")]
    public async Task<IActionResult> GetAttributeOptions(int attributeId)
    {
        var attr = await _db.Attributes
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .FirstOrDefaultAsync(a => a.Id == attributeId);

        if (attr == null) return NotFound(new { message = "Attribute not found." });

        return Ok(new
        {
            data = attr.Options.Select(o => new
            {
                o.Id,
                Label = o.Translations.FirstOrDefault(t => t.Locale == _locale)?.Label ?? o.AdminName,
                o.SortOrder
            })
        });
    }

    /// <summary>Get main store categories (Food Stores, Build Stores, Local Stores, Local Services)</summary>
    [HttpGet("main")]
    public async Task<IActionResult> GetMainCategories()
    {
        // Match by slug rather than ID so re-imports that renumber rows don't silently drop stores.
        var mainSlugs = new[] { "dd-foodstore", "dd-buildstore", "local-store", "local-services" };

        var matchedIds = await _db.CategoryTranslations
            .Where(t => mainSlugs.Contains(t.Slug) && t.Locale == _locale)
            .Select(t => t.CategoryId)
            .Distinct()
            .ToListAsync();

        var mainCats = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Children).ThenInclude(ch => ch.Translations)
            .Where(c => matchedIds.Contains(c.Id) && c.Status)
            .ToListAsync();

        int SlugOrder(Category c)
        {
            var slug = c.Translations.FirstOrDefault(t => t.Locale == _locale)?.Slug
                       ?? c.Translations.FirstOrDefault()?.Slug
                       ?? "";
            var idx = Array.IndexOf(mainSlugs, slug);
            return idx < 0 ? int.MaxValue : idx;
        }
        mainCats = mainCats.OrderBy(SlugOrder).ToList();

        var data = mainCats.Select(c =>
        {
            var t = c.Translations.FirstOrDefault(t => t.Locale == _locale)
                    ?? c.Translations.FirstOrDefault();
            return new
            {
                c.Id,
                Name = t?.Name,
                Slug = t?.Slug,
                Description = t?.Description,
                c.LogoPath,
                c.BannerPath,
                ChildCount = c.Children.Count(ch => ch.Status),
                Children = c.Children.Where(ch => ch.Status).OrderBy(ch => ch.Position).Select(ch =>
                {
                    var ct = ch.Translations.FirstOrDefault(t => t.Locale == _locale)
                             ?? ch.Translations.FirstOrDefault();
                    return new
                    {
                        ch.Id,
                        Name = ct?.Name,
                        Slug = ct?.Slug,
                        Description = ct?.Description,
                        ch.LogoPath,
                        ch.BannerPath
                    };
                })
            };
        });

        return Ok(new { data });
    }

    /// <summary>Get products belonging to a category</summary>
    [HttpGet("{id:int}/products")]
    public async Task<IActionResult> GetCategoryProducts(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 10)
    {
        var exists = await _db.Categories.AnyAsync(c => c.Id == id);
        if (!exists)
            return NotFound(new { message = "Category not found." });

        // One SQL round-trip to walk the whole sub-tree via parent_id. Replaces
        // the old recursive CollectChildIds (which ran one query per descendant).
        // _lft/_rgt nested-set columns on this DB are stale, so we traverse
        // parent_id — MySQL 8+ supports recursive CTEs natively.
        var allCategoryIds = await _db.Database
            .SqlQueryRaw<int>(
                @"WITH RECURSIVE subtree AS (
                      SELECT id FROM categories WHERE id = {0} AND status = 1
                      UNION ALL
                      SELECT c.id FROM categories c
                      INNER JOIN subtree s ON c.parent_id = s.id
                      WHERE c.status = 1
                  )
                  SELECT id AS `Value` FROM subtree",
                id)
            .ToListAsync();

        await _productService.EnsureAttrIdsAsync();

        // Build the base filter without Includes so CountAsync emits a plain
        // COUNT(*) (no cartesian blow-up from the collection joins).
        var baseQ = _db.Products
            .Where(p => p.Categories.Any(c => allCategoryIds.Contains(c.Id)));

        var totalCount = await baseQ.CountAsync();
        var offset = (page - 1) * limit;

        // Paginate first, THEN join child tables with AsSplitQuery so each
        // collection comes back as its own SELECT (no cartesian) and AsNoTracking
        // skips change-tracking overhead for this read-only projection.
        // Tie-break by Id so ordering is deterministic — required for AsSplitQuery,
        // otherwise the secondary queries for Flats/Images/etc. can pick a different
        // set of parent rows than the main query when CreatedAt is not unique,
        // which leaves child collections empty (or attached to the wrong parent).
        var products = await baseQ
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip(offset)
            .Take(limit)
            .Include(p => p.AttributeValues)
            .Include(p => p.Images.OrderBy(i => i.Position))
            .Include(p => p.Reviews.Where(r => r.Status == "approved"))
            .Include(p => p.Inventories)
            .Include(p => p.Flats)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync();

        var data = products.Select(p =>
        {
            // Try EAV first, fall back to product_flat
            var name = _productService.GetProductName(p);
            var price = _productService.GetProductPrice(p);
            var specialPrice = _productService.GetProductSpecialPrice(p);
            var urlKey = _productService.GetProductUrlKey(p);
            var shortDesc = _productService.GetProductShortDescription(p);

            // If EAV has no data, try product_flat
            if (name == p.Sku)
            {
                var flat = p.Flats.FirstOrDefault(f => f.Locale == _locale)
                           ?? p.Flats.FirstOrDefault();
                if (flat != null)
                {
                    name = flat.Name ?? name;
                    if (price == 0 && flat.Price.HasValue) price = flat.Price.Value;
                    if (!specialPrice.HasValue && flat.SpecialPrice.HasValue) specialPrice = flat.SpecialPrice;
                    urlKey ??= flat.UrlKey;
                    shortDesc ??= flat.ShortDescription;
                }
            }

            var effectivePrice = (specialPrice.HasValue && specialPrice > 0)
                ? specialPrice.Value : price;

            return new
            {
                p.Id,
                p.Sku,
                Name = name,
                Description = shortDesc,
                Price = price,
                SpecialPrice = specialPrice,
                FormattedPrice = $"₹{effectivePrice:N2}",
                UrlKey = urlKey,
                BaseImage = _productService.GetBaseImageUrl(p)
                            ?? p.Images.FirstOrDefault()?.Path,
                Images = p.Images.Select(i =>
                    _productService.GetImagePublicPath(i) ?? i.Path),
                InStock = _productService.IsSaleable(p),
                // Tells the storefront whether tapping the card's ADD button
                // can add directly (false) or must open a variant picker
                // (true). Driven off the product type seeded by
                // DigitalDarsiSeeder when the scraped data carried real
                // option chips.
                HasVariants = p.Type == "configurable",
            };
        });

        return Ok(new
        {
            data,
            meta = new
            {
                total = totalCount,
                currentPage = page,
                perPage = limit,
                lastPage = (int)Math.Ceiling((double)totalCount / limit)
            }
        });
    }


    /// <summary>Get maximum product price for a category</summary>
    [HttpGet("max-price/{id:int?}")]
    public async Task<IActionResult> GetMaxPrice(int? id)
    {
        var priceAttr = await _db.Attributes.FirstOrDefaultAsync(a => a.Code == "price");
        if (priceAttr == null) return Ok(new { maxPrice = 0 });

        IQueryable<Models.Catalog.Product> q = _db.Products.Include(p => p.AttributeValues);

        if (id.HasValue)
            q = q.Where(p => p.Categories.Any(c => c.Id == id));

        var maxPrice = await q
            .SelectMany(p => p.AttributeValues.Where(v => v.AttributeId == priceAttr.Id))
            .MaxAsync(v => (decimal?)v.FloatValue) ?? 0;

        return Ok(new { maxPrice });
    }
}
