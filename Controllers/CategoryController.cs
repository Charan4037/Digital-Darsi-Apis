using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers;

[ApiController]
[Route("api/v1/categories")]
[Tags("Categories")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class CategoryController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;
    private readonly string _locale;
    private readonly string _baseUrl;

    public CategoryController(DOSDbContext db, ProductService productService, IConfiguration config, LocaleContext localeCtx)
    {
        _db = db;
        _productService = productService;
        _locale = localeCtx.Locale;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
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
                .AsSplitQuery()
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

    /// <summary>Get the 4 main store categories (Build Store, Food Store, General Store, Services).</summary>
    [HttpGet("main")]
    public async Task<IActionResult> GetMainCategories()
    {
        // Match by slug rather than ID so re-imports that renumber rows don't silently drop stores.
        // These four slugs are produced by DigitalDarsiSeeder for the four
        // Digital Darsi source sites (buildstore / foodstore / store / services).
        // The order here is the order the app renders the storefront tabs in —
        // and the app selects the first one on launch (so Food Store opens by
        // default).
        var mainSlugs = new[] { "dd-foodstore", "dd-store", "dd-buildstore", "dd-services" };

        var matchedIds = await _db.CategoryTranslations
            .Where(t => mainSlugs.Contains(t.Slug) && t.Locale == _locale)
            .Select(t => t.CategoryId)
            .Distinct()
            .ToListAsync();

        var mainCats = await _db.Categories
            .AsSplitQuery()
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

    /// <summary>
    /// One screen of the nested catalogue, mirroring how the website drills
    /// down: tapping a category shows the direct sub-categories <em>and</em>
    /// the products that sit at that level (exactly that level — not the whole
    /// sub-tree). The app calls this for every category screen below the
    /// storefront landing (which still comes from <c>GET /main</c>):
    ///   <list type="bullet">
    ///     <item>store → its top categories (Electronics, …)</item>
    ///     <item>Electronics → its sub-categories (Heating &amp; Cooling, …)
    ///           + Electronics' own products</item>
    ///     <item>Heating &amp; Cooling → its sub-categories + its products</item>
    ///   </list>
    /// A leaf category simply comes back with an empty <c>subcategories</c>
    /// list and just its products. <c>breadcrumb</c> carries the ancestor
    /// chain so the app can render the back-trail.
    /// </summary>
    [HttpGet("{id:int}/browse")]
    public async Task<IActionResult> Browse(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 10)
    {
        // Whole (active) category table — small, and we need it for the
        // breadcrumb walk, the hasChildren flags and the sub-tree counts.
        var allCats = await _db.Categories
            .Include(c => c.Translations)
            .Where(c => c.Status)
            .OrderBy(c => c.Position)
            .ToListAsync();

        var catById = allCats.ToDictionary(c => c.Id);
        if (!catById.TryGetValue(id, out var category))
            return NotFound(new { message = "Category not found." });

        var childrenByParent = allCats
            .Where(c => c.ParentId != null)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Position).ToList());

        // ─── Breadcrumb: walk up parent_id, skipping the synthetic Root ───
        var breadcrumb = new List<object>();
        var cursor = category.ParentId;
        var guard = 0;
        Category? storeCat = null;
        while (cursor != null && guard++ < 50 && catById.TryGetValue(cursor.Value, out var ancestor))
        {
            // The Root category (parent_id IS NULL) is an internal anchor, not
            // a browsable store — leave it out of the trail.
            if (ancestor.ParentId != null)
            {
                breadcrumb.Add(FormatCategoryBrief(ancestor));
                // The topmost non-root ancestor is the store this category
                // belongs to (Build/Food/General Store, Services).
                storeCat = ancestor;
            }
            cursor = ancestor.ParentId;
        }
        breadcrumb.Reverse();
        // When the category has no ancestors it IS a top-level store category.
        storeCat ??= category;

        // ─── Store's top-level categories (the app's Flipkart-style tabs) ──
        var mainCats = childrenByParent.TryGetValue(storeCat.Id, out var mcs)
            ? mcs
            : new List<Category>();
        var mainCategories = mainCats.Select(m => new
        {
            m.Id,
            Name = CategoryName(m),
            Slug = CategorySlug(m),
            m.LogoPath,
            LogoUrl = ResolveAssetUrl(m.LogoPath),
        }).ToList();

        // ─── Direct sub-categories of this category ───────────────────────
        var directChildren = childrenByParent.TryGetValue(id, out var kids)
            ? kids
            : new List<Category>();

        // Sub-tree product counts so each sub-category tile can show how many
        // products live under it (its own + every descendant's).
        var countByChild = await CountProductsPerSubtreeAsync(directChildren, childrenByParent);

        var subcategories = directChildren.Select(ch => new
        {
            ch.Id,
            Name = CategoryName(ch),
            Slug = CategorySlug(ch),
            Description = CategoryDescription(ch),
            ch.LogoPath,
            LogoUrl = ResolveAssetUrl(ch.LogoPath),
            ch.BannerPath,
            BannerUrl = ResolveAssetUrl(ch.BannerPath),
            HasChildren = childrenByParent.ContainsKey(ch.Id),
            ProductCount = countByChild.TryGetValue(ch.Id, out var n) ? n : 0
        }).ToList();

        // ─── Products in this category's whole sub-tree ───────────────────
        // Mirrors the website: a category page lists every product beneath it
        // (itself + all descendants) while the sub-category tiles above let
        // the user narrow down. A leaf category naturally shows just its own.
        //
        // Store-root filter: when browsing a top-level store category (its
        // parent is the global root), skip products whose only category link
        // is to this store root itself. Those products landed there because
        // the scraper couldn't match their breadcrumbs to any subcategory —
        // they are often mismatched products from other store domains that
        // happen to be visible on this site. The website avoids the problem
        // by serving category pages; we mirror that by requiring a real
        // subcategory placement.
        var subtreeIds = await GetSubtreeCategoryIdsAsync(id);
        var globalRoot = catById.Values.FirstOrDefault(c => c.ParentId == null);
        var isStoreRoot = globalRoot != null && category.ParentId == globalRoot.Id;
        var (productData, total) = await QueryCategoryProductsAsync(
            subtreeIds, page, limit,
            storeRootId: isStoreRoot ? id : null);

        return Ok(new
        {
            category = new
            {
                category.Id,
                Name = CategoryName(category),
                Slug = CategorySlug(category),
                Description = CategoryDescription(category),
                category.ParentId,
                category.LogoPath,
                LogoUrl = ResolveAssetUrl(category.LogoPath),
                category.BannerPath,
                BannerUrl = ResolveAssetUrl(category.BannerPath)
            },
            breadcrumb,
            store = FormatCategoryBrief(storeCat),
            mainCategories,
            subcategories,
            hasSubcategories = subcategories.Count > 0,
            products = new
            {
                data = productData,
                meta = new
                {
                    total,
                    currentPage = page,
                    perPage = limit,
                    lastPage = (int)Math.Ceiling((double)total / Math.Max(limit, 1))
                }
            }
        });
    }

    /// <summary>Get products belonging to a category.</summary>
    /// <param name="descendants">
    /// When <c>true</c> (default, kept for backward compatibility) the result
    /// includes products from the whole category sub-tree. When <c>false</c>
    /// only products attached directly to this category are returned — the
    /// behaviour the nested storefront navigation expects.
    /// </param>
    [HttpGet("{id:int}/products")]
    public async Task<IActionResult> GetCategoryProducts(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 10,
        [FromQuery] bool descendants = true)
    {
        var exists = await _db.Categories.AnyAsync(c => c.Id == id);
        if (!exists)
            return NotFound(new { message = "Category not found." });

        var categoryIds = descendants
            ? await GetSubtreeCategoryIdsAsync(id)
            : new List<int> { id };

        // Apply the same store-root filter as Browse: exclude products that
        // are only in the store root when querying a store's whole catalogue.
        int? storeRootId = null;
        if (descendants)
        {
            var cat = await _db.Categories
                .Include(c => c.Translations)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (cat != null)
            {
                var globalRoot = await _db.Categories.FirstOrDefaultAsync(c => c.ParentId == null);
                if (globalRoot != null && cat.ParentId == globalRoot.Id)
                    storeRootId = id;
            }
        }

        var (data, totalCount) = await QueryCategoryProductsAsync(categoryIds, page, limit, storeRootId);

        return Ok(new
        {
            data,
            meta = new
            {
                total = totalCount,
                currentPage = page,
                perPage = limit,
                lastPage = (int)Math.Ceiling((double)totalCount / Math.Max(limit, 1))
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

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Walks the whole sub-tree of <paramref name="id"/> in memory.
    /// Loads only (id, parent_id) — one lightweight query, no MySQL version
    /// requirement. Replaces the previous WITH RECURSIVE CTE which only works
    /// on MySQL 8+; the prod server runs MySQL 5.7.</summary>
    private async Task<List<int>> GetSubtreeCategoryIdsAsync(int id)
    {
        var rows = await _db.Categories
            .Where(c => c.Status)
            .Select(c => new { c.Id, c.ParentId })
            .AsNoTracking()
            .ToListAsync();

        var childMap = rows
            .Where(r => r.ParentId != null)
            .GroupBy(r => r.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList());

        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            result.Add(cur);
            if (childMap.TryGetValue(cur, out var kids))
                foreach (var k in kids) stack.Push(k);
        }
        return result;
    }

    /// <summary>Runs the paginated product query for the given set of category
    /// IDs and projects each row into the storefront card shape. Shared by
    /// <see cref="Browse"/> (single category) and
    /// <see cref="GetCategoryProducts"/> (single category or whole sub-tree).
    /// <para>
    /// When <paramref name="storeRootId"/> is set the query additionally
    /// requires the product to have at least one category link that is
    /// <em>not</em> the store root itself, filtering out products that landed
    /// only at the store root because the scraper couldn't match their
    /// breadcrumbs to any subcategory (cross-domain contamination).
    /// </para></summary>
    private async Task<(List<object> data, int totalCount)> QueryCategoryProductsAsync(
        IReadOnlyCollection<int> categoryIds, int page, int limit,
        int? storeRootId = null)
    {
        await _productService.EnsureAttrIdsAsync();

        // Exclude child variant products (parent_id != null) — the configurable
        // parent is the catalogue face. Build the base filter without Includes
        // so CountAsync emits a plain COUNT(*) (no cartesian blow-up).
        var baseQ = _db.Products
            .Where(p => p.ParentId == null
                && p.Categories.Any(c => categoryIds.Contains(c.Id))
                && p.Flats.Any(f => f.Status == true));

        // When browsing a store root, require a real subcategory placement so
        // mismatched cross-domain products (seeded only at the store root)
        // don't pollute the listing. Products legitimately in a subcategory are
        // unaffected because they pass the inner Any() even if they also
        // happen to be linked to the root.
        if (storeRootId.HasValue)
        {
            var rootId = storeRootId.Value;
            baseQ = baseQ.Where(p =>
                p.Categories.Any(c => categoryIds.Contains(c.Id) && c.Id != rootId));
        }

        var totalCount = await baseQ.CountAsync();
        var offset = (Math.Max(page, 1) - 1) * limit;

        // Paginate first, THEN join child tables with AsSplitQuery so each
        // collection comes back as its own SELECT (no cartesian) and AsNoTracking
        // skips change-tracking overhead. Tie-break by Id so ordering is
        // deterministic — required for AsSplitQuery.
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
            .Include(p => p.Children).ThenInclude(c => c.AttributeValues)
            .Include(p => p.Children).ThenInclude(c => c.Flats)
            .Include(p => p.Children).ThenInclude(c => c.Inventories)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync();

        var data = products.Select(BuildProductCard).ToList();
        return (data, totalCount);
    }

    /// <summary>Projects a product into the storefront list-card shape. Reads
    /// EAV attribute values first, falls back to product_flat.</summary>
    private object BuildProductCard(Product p)
    {
        var name = _productService.GetProductName(p);
        var pricingProduct = _productService.GetPricingProduct(p);
        var price = _productService.GetProductPrice(pricingProduct);
        var specialPrice = _productService.GetProductSpecialPrice(pricingProduct);
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
                urlKey ??= flat.UrlKey;
                shortDesc ??= flat.ShortDescription;
            }
        }
        if (price == 0 || !specialPrice.HasValue)
        {
            var pricingFlat = pricingProduct.Flats.FirstOrDefault(f => f.Locale == _locale)
                               ?? pricingProduct.Flats.FirstOrDefault();
            if (pricingFlat != null)
            {
                if (price == 0 && pricingFlat.Price.HasValue) price = pricingFlat.Price.Value;
                if (!specialPrice.HasValue && pricingFlat.SpecialPrice.HasValue) specialPrice = pricingFlat.SpecialPrice;
            }
        }

        var effectivePrice = (specialPrice.HasValue && specialPrice > 0)
            ? specialPrice.Value : price;

        var reviewCount = p.Reviews.Count;
        var avgRating = reviewCount > 0 ? p.Reviews.Average(r => r.Rating) : 0.0;

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
            InStock = _productService.IsSaleable(pricingProduct),
            HasVariants = p.Type == "configurable",
            AverageRating = avgRating,
            ReviewsCount = reviewCount,
        };
    }

    /// <summary>For each category in <paramref name="children"/>, counts the
    /// distinct products living anywhere in that category's sub-tree (itself
    /// + every descendant). Two DB round-trips total regardless of how many
    /// children there are.</summary>
    private async Task<Dictionary<int, int>> CountProductsPerSubtreeAsync(
        List<Category> children,
        Dictionary<int, List<Category>> childrenByParent)
    {
        var result = new Dictionary<int, int>();
        if (children.Count == 0) return result;

        // Sub-tree category-id set for each child (walked in memory).
        var subtreeByChild = new Dictionary<int, HashSet<int>>();
        var everyCatId = new HashSet<int>();
        foreach (var child in children)
        {
            var set = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(child.Id);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!set.Add(cur)) continue;
                everyCatId.Add(cur);
                if (childrenByParent.TryGetValue(cur, out var grandKids))
                    foreach (var gk in grandKids) stack.Push(gk.Id);
            }
            subtreeByChild[child.Id] = set;
        }

        // One query: every non-variant product touching any category in the
        // combined sub-tree, with the category ids it is filed under.
        var links = await _db.Products
            .Where(p => p.ParentId == null
                && p.Categories.Any(c => everyCatId.Contains(c.Id))
                && p.Flats.Any(f => f.Status == true))
            .Select(p => new { p.Id, CatIds = p.Categories.Select(c => c.Id).ToList() })
            .AsNoTracking()
            .ToListAsync();

        foreach (var child in children)
        {
            var subtree = subtreeByChild[child.Id];
            result[child.Id] = links.Count(l => l.CatIds.Any(subtree.Contains));
        }
        return result;
    }

    private object FormatCategoryBrief(Category c) => new
    {
        c.Id,
        Name = CategoryName(c),
        Slug = CategorySlug(c)
    };

    private string? CategoryName(Category c) =>
        (c.Translations.FirstOrDefault(t => t.Locale == _locale)
         ?? c.Translations.FirstOrDefault())?.Name;

    private string? CategorySlug(Category c) =>
        (c.Translations.FirstOrDefault(t => t.Locale == _locale)
         ?? c.Translations.FirstOrDefault())?.Slug;

    private string? CategoryDescription(Category c) =>
        (c.Translations.FirstOrDefault(t => t.Locale == _locale)
         ?? c.Translations.FirstOrDefault())?.Description;

    /// <summary>Turns a stored logo/banner path into an absolute URL. Scraped
    /// category images are already full URLs, so those pass through untouched;
    /// relative paths are resolved against the DOS storage mount.</summary>
    private string? ResolveAssetUrl(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http://") || path.StartsWith("https://")) return path;
        return $"{_baseUrl}/storage/{path}";
    }
}
