using HotChocolate;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.GraphQL.Types;
using DOSApi.Models;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.GraphQL.Queries;

[ExtendObjectType("Query")]
public class ProductQueries
{
    public async Task<List<TreeCategoryResult>> GetTreeCategories(
        [Service] DOSDbContext db,
        [Service] IConfiguration config,
        int? parentId = null)
    {
        var locale = config["App:Locale"] ?? "en";
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        var query = db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Children).ThenInclude(c => c.Translations)
            .Include(c => c.Children).ThenInclude(c => c.Children).ThenInclude(c => c.Translations)
            .Where(c => c.Status);

        if (parentId.HasValue)
            query = query.Where(c => c.ParentId == parentId);
        else
            query = query.Where(c => c.ParentId == null || c.ParentId == 1);

        var cats = await query.OrderBy(c => c.Position).ToListAsync();

        return cats.Select(c => MapCategory(c, locale, baseUrl)).ToList();
    }

    /// <summary>
    /// One screen of the nested catalogue — the GraphQL twin of the REST
    /// <c>GET /api/v1/categories/{id}/browse</c> endpoint. Mirrors how the
    /// website drills down: tapping a category returns its direct
    /// sub-categories <em>and</em> the products that sit at exactly that level
    /// (not the whole sub-tree). The app calls this for every category screen:
    /// store → Electronics → Household and appliances → … → Air Coolers.
    /// A leaf category comes back with an empty <c>subcategories</c> list and
    /// just its products. Returns <c>null</c> when the category id is unknown.
    /// </summary>
    public async Task<CategoryBrowseResult?> GetCategoryBrowse(
        [Service] ProductService svc,
        [Service] DOSDbContext db,
        [Service] IConfiguration config,
        int id,
        int? first = 10,
        string? after = null)
    {
        var locale = config["App:Locale"] ?? "en";
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        // Whole (active) category table — small, and needed for the breadcrumb
        // walk, the hasChildren flags and the sub-tree counts.
        var allCats = await db.Categories
            .Include(c => c.Translations)
            .Where(c => c.Status)
            .OrderBy(c => c.Position)
            .ToListAsync();

        var catById = allCats.ToDictionary(c => c.Id);
        if (!catById.TryGetValue(id, out var category)) return null;

        var childrenByParent = allCats
            .Where(c => c.ParentId != null)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Position).ToList());

        // ─── Breadcrumb: walk up parent_id, skipping the synthetic Root ───
        var breadcrumb = new List<CategoryBriefResult>();
        var cursor = category.ParentId;
        var guard = 0;
        Category? storeCat = null;
        while (cursor != null && guard++ < 50 && catById.TryGetValue(cursor.Value, out var ancestor))
        {
            // The Root category (parent_id IS NULL) is an internal anchor,
            // not a browsable store — leave it out of the trail.
            if (ancestor.ParentId != null)
            {
                breadcrumb.Add(ToCategoryBrief(ancestor, locale));
                // Topmost non-root ancestor = the store this category lives in.
                storeCat = ancestor;
            }
            cursor = ancestor.ParentId;
        }
        breadcrumb.Reverse();
        // No ancestors → the category itself is a top-level store category.
        storeCat ??= category;

        // ─── Store's top-level categories (the app's Flipkart-style tabs) ──
        var mainCategories = (childrenByParent.TryGetValue(storeCat.Id, out var mcs)
                ? mcs
                : new List<Category>())
            .Select(m =>
            {
                var mt = m.Translations.FirstOrDefault(x => x.Locale == locale)
                         ?? m.Translations.FirstOrDefault();
                return new CategoryBriefResult
                {
                    Id = m.Id,
                    Name = mt?.Name,
                    Slug = mt?.Slug,
                    LogoUrl = ResolveAssetUrl(m.LogoPath, baseUrl),
                };
            })
            .ToList();

        // ─── Direct sub-categories of this category ───────────────────────
        var directChildren = childrenByParent.TryGetValue(id, out var kids)
            ? kids
            : new List<Category>();

        var countByChild = await CountProductsPerSubtreeAsync(db, directChildren, childrenByParent);

        var subcategories = directChildren.Select(ch =>
        {
            var t = ch.Translations.FirstOrDefault(x => x.Locale == locale)
                    ?? ch.Translations.FirstOrDefault();
            return new BrowseSubcategoryResult
            {
                Id = ch.Id,
                Name = t?.Name,
                Slug = t?.Slug,
                Description = t?.Description,
                LogoPath = ch.LogoPath,
                LogoUrl = ResolveAssetUrl(ch.LogoPath, baseUrl),
                BannerUrl = ResolveAssetUrl(ch.BannerPath, baseUrl),
                HasChildren = childrenByParent.ContainsKey(ch.Id),
                ProductCount = countByChild.TryGetValue(ch.Id, out var n) ? n : 0
            };
        }).ToList();

        // ─── Products in this category's whole sub-tree ───────────────────
        // Mirrors the website: a category page lists every product beneath it
        // (itself + all descendants); the sub-category tiles narrow it down.
        var subtreeIds = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!subtreeIds.Add(cur)) continue;
            if (childrenByParent.TryGetValue(cur, out var ck))
                foreach (var k in ck) stack.Push(k.Id);
        }

        var offset = ConnectionHelper.DecodeCursor(after);
        var limit = first ?? 10;
        await svc.EnsureAttrIdsAsync();
        var baseQ = db.Products
            .Where(p => p.ParentId == null && p.Categories.Any(c => subtreeIds.Contains(c.Id)));
        var totalCount = await baseQ.CountAsync();
        var items = await baseQ
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
        var productResults = items.Select(p => MapProductFromAttributes(p, baseUrl, svc)).ToList();
        var products = ConnectionHelper.ToConnection(productResults, totalCount, offset, limit);

        var catT = category.Translations.FirstOrDefault(x => x.Locale == locale)
                   ?? category.Translations.FirstOrDefault();

        return new CategoryBrowseResult
        {
            Category = new BrowseCategoryResult
            {
                Id = category.Id,
                Name = catT?.Name,
                Slug = catT?.Slug,
                Description = catT?.Description,
                ParentId = category.ParentId,
                LogoPath = category.LogoPath,
                LogoUrl = ResolveAssetUrl(category.LogoPath, baseUrl),
                BannerUrl = ResolveAssetUrl(category.BannerPath, baseUrl)
            },
            Breadcrumb = breadcrumb,
            Store = ToCategoryBrief(storeCat, locale),
            MainCategories = mainCategories,
            Subcategories = subcategories,
            HasSubcategories = subcategories.Count > 0,
            Products = products
        };
    }

    public async Task<Connection<ProductResult>> GetProducts(
        [Service] ProductService svc,
        [Service] DOSDbContext db,
        [Service] IConfiguration config,
        string? query = null, string? sortKey = null, bool reverse = false,
        int? first = 10, int? last = null, string? after = null, string? before = null,
        string? channel = null, string? locale = null, string? filter = null)
    {
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        var offset = ConnectionHelper.DecodeCursor(after);
        var limit = first ?? 10;

        var (items, totalCount) = await svc.QueryProductsAsync(filter, sortKey, reverse, query, offset, limit);

        var results = items.Select(p => MapProductFromAttributes(p, baseUrl, svc)).ToList();
        return ConnectionHelper.ToConnection(results, totalCount, offset, limit);
    }

    public async Task<ProductDetailResult?> GetProduct(
        [Service] ProductService svc,
        [Service] IConfiguration config,
        string urlKey)
    {
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
        var loc = config["App:Locale"] ?? "en";

        var product = await svc.GetProductByUrlKeyAsync(urlKey);
        if (product == null) return null;

        return MapProductDetail(product, baseUrl, loc, svc);
    }

    public async Task<Connection<ChannelResult>> GetChannels(
        [Service] DOSDbContext db,
        [Service] IConfiguration config,
        int first = 1)
    {
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
        var channels = await db.Channels.Include(c => c.Translations).Take(first).ToListAsync();

        var results = channels.Select(c => new ChannelResult
        {
            Id = c.Id,
            Code = c.Code,
            LogoUrl = c.Logo != null ? $"{baseUrl}/storage/{c.Logo}" : null,
            FaviconUrl = c.Favicon != null ? $"{baseUrl}/storage/{c.Favicon}" : null,
            Hostname = c.Hostname,
            Translation = c.Translations.FirstOrDefault() is { } t
                ? new TranslationResult { Id = t.Id, Name = t.Name }
                : null
        }).ToList();

        return ConnectionHelper.ToConnection(results, results.Count, 0, first);
    }

    public async Task<Connection<ThemeCustomizationResult>> GetThemeCustomizations(
        [Service] DOSDbContext db, int? first = 20)
    {
        var themes = await db.ThemeCustomizations
            .Include(t => t.Translations)
            .Where(t => t.Status)
            .OrderBy(t => t.SortOrder)
            .Take(first ?? 20)
            .ToListAsync();

        var results = themes.Select(t => new ThemeCustomizationResult
        {
            Id = t.Id,
            Type = t.Type,
            Name = t.Name,
            Status = t.Status,
            SortOrder = t.SortOrder,
            Translations = new Connection<ThemeTranslationResult>
            {
                Edges = t.Translations.Select((tr, i) => new Edge<ThemeTranslationResult>
                {
                    Node = new ThemeTranslationResult
                    {
                        Id = tr.Id,
                        ThemeCustomizationId = tr.ThemeCustomizationId,
                        Locale = tr.Locale,
                        Options = tr.Options
                    },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(i.ToString()))
                }).ToList(),
                TotalCount = t.Translations.Count,
                PageInfo = new PageInfo()
            }
        }).ToList();

        return ConnectionHelper.ToConnection(results, results.Count, 0, first ?? 20);
    }

    public async Task<AttributeResult?> GetAttribute([Service] DOSDbContext db, string id)
    {
        // Parse IRI or numeric ID
        var numId = 0;
        if (id.Contains('/'))
        {
            var parts = id.Split('/');
            int.TryParse(parts.Last(), out numId);
        }
        else int.TryParse(id, out numId);

        var attr = await db.Attributes
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .FirstOrDefaultAsync(a => a.Id == numId);

        if (attr == null) return null;

        return new AttributeResult
        {
            Id = attr.Id,
            Code = attr.Code,
            Options = new Connection<AttributeOptionResult>
            {
                Edges = attr.Options.Select((o, i) => new Edge<AttributeOptionResult>
                {
                    Node = new AttributeOptionResult
                    {
                        Id = o.Id,
                        AdminName = o.AdminName,
                        Translations = new Connection<OptionTranslationResult>
                        {
                            Edges = o.Translations.Select((t, j) => new Edge<OptionTranslationResult>
                            {
                                Node = new OptionTranslationResult { Id = t.Id, Label = t.Label, Locale = t.Locale },
                                Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(j.ToString()))
                            }).ToList(),
                            TotalCount = o.Translations.Count,
                            PageInfo = new PageInfo()
                        }
                    },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(i.ToString()))
                }).ToList(),
                TotalCount = attr.Options.Count,
                PageInfo = new PageInfo()
            }
        };
    }

    public async Task<Connection<CategoryAttributeFilterResult>> GetCategoryAttributeFilters(
        [Service] DOSDbContext db,
        [Service] IConfiguration config,
        string? categorySlug = null, int? first = 20)
    {
        var locale = config["App:Locale"] ?? "en";

        IQueryable<Models.Catalog.Attribute> query;

        if (!string.IsNullOrEmpty(categorySlug))
        {
            var cat = await db.CategoryTranslations.Where(ct => ct.Slug == categorySlug).Select(ct => ct.CategoryId).FirstOrDefaultAsync();
            if (cat > 0)
            {
                query = db.Categories
                    .Where(c => c.Id == cat)
                    .SelectMany(c => c.FilterableAttributes);
            }
            else
            {
                query = db.Attributes.Where(a => a.IsFilterable);
            }
        }
        else
        {
            query = db.Attributes.Where(a => a.IsFilterable);
        }

        var attrs = await query
            .Include(a => a.Translations)
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .Take(first ?? 20)
            .ToListAsync();

        // Get price range
        var minPrice = await db.ProductFlats.Where(p => p.Price > 0).MinAsync(p => (decimal?)p.Price) ?? 0;
        var maxPrice = await db.ProductFlats.Where(p => p.Price > 0).MaxAsync(p => (decimal?)p.Price) ?? 0;

        var results = attrs.Select(a => new CategoryAttributeFilterResult
        {
            Id = $"/api/shop/attributes/{a.Id}",
            _Id = a.Id,
            Code = a.Code,
            AdminName = a.AdminName,
            Type = a.Type,
            SwatchType = a.SwatchType,
            Validation = a.Validation,
            Position = a.Position,
            IsRequired = a.IsRequired,
            IsUnique = a.IsUnique,
            IsFilterable = a.IsFilterable,
            IsComparable = a.IsComparable,
            IsConfigurable = a.IsConfigurable,
            IsUserDefined = a.IsUserDefined,
            IsVisibleOnFront = a.IsVisibleOnFront,
            ValuePerLocale = a.ValuePerLocale,
            ValuePerChannel = a.ValuePerChannel,
            DefaultValue = a.DefaultValue?.ToString(),
            MaxPrice = a.Code == "price" ? maxPrice : null,
            MinPrice = a.Code == "price" ? minPrice : null,
            Translations = a.Translations.Select(t => new AttrTransResult
            {
                Id = $"/api/shop/attribute-translations/{t.Id}",
                _Id = t.Id,
                AttributeId = t.AttributeId,
                Locale = t.Locale,
                Name = t.Name
            }).ToList(),
            Options = a.Options.Select(o => new AttrOptionFullResult
            {
                Id = $"/api/shop/attribute-options/{o.Id}",
                _Id = o.Id,
                AdminName = o.AdminName,
                SortOrder = o.SortOrder,
                SwatchValue = o.SwatchValue,
                Translation = o.Translations.FirstOrDefault(t => t.Locale == locale) is { } tr
                    ? new OptionTransResult { Id = tr.Id, _Id = tr.Id, AttributeOptionId = tr.AttributeOptionId, Locale = tr.Locale, Label = tr.Label }
                    : null,
                Translations = o.Translations.Select(t => new OptionTransResult
                {
                    Id = t.Id, _Id = t.Id, AttributeOptionId = t.AttributeOptionId, Locale = t.Locale, Label = t.Label
                }).ToList()
            }).ToList()
        }).ToList();

        return ConnectionHelper.ToConnection(results, results.Count, 0, first ?? 20);
    }

    // ─── Helper mapping methods ─────────────────────────────────

    private static CategoryBriefResult ToCategoryBrief(Category c, string locale)
    {
        var t = c.Translations.FirstOrDefault(x => x.Locale == locale)
                ?? c.Translations.FirstOrDefault();
        return new CategoryBriefResult { Id = c.Id, Name = t?.Name, Slug = t?.Slug };
    }

    /// <summary>Turns a stored logo/banner path into an absolute URL. Scraped
    /// category images are already full URLs, so those pass through untouched;
    /// relative paths are resolved against the DOS storage mount.</summary>
    private static string? ResolveAssetUrl(string? path, string baseUrl)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http://") || path.StartsWith("https://")) return path;
        return $"{baseUrl.TrimEnd('/')}/storage/{path}";
    }

    /// <summary>For each category in <paramref name="children"/>, counts the
    /// distinct products living anywhere in that category's sub-tree (itself
    /// + every descendant). Two DB round-trips total regardless of how many
    /// children there are.</summary>
    private static async Task<Dictionary<int, int>> CountProductsPerSubtreeAsync(
        DOSDbContext db,
        List<Category> children,
        Dictionary<int, List<Category>> childrenByParent)
    {
        var result = new Dictionary<int, int>();
        if (children.Count == 0) return result;

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

        var links = await db.Products
            .Where(p => p.ParentId == null && p.Categories.Any(c => everyCatId.Contains(c.Id)))
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

    private static TreeCategoryResult MapCategory(Category c, string locale, string baseUrl)
    {
        var t = c.Translations.FirstOrDefault(t => t.Locale == locale) ?? c.Translations.FirstOrDefault();
        return new TreeCategoryResult
        {
            Id = c.Id,
            _Id = c.Id,
            Position = c.Position,
            LogoPath = c.LogoPath,
            LogoUrl = c.LogoPath != null ? $"{baseUrl}/storage/{c.LogoPath}" : null,
            BannerUrl = c.BannerPath != null ? $"{baseUrl}/storage/{c.BannerPath}" : null,
            Status = c.Status,
            Translation = t != null ? new CategoryTranslationResult
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                Description = t.Description,
                UrlPath = t.UrlPath,
                MetaTitle = t.MetaTitle,
                _Id = t.Id
            } : null,
            Children = c.Children.Where(ch => ch.Status).OrderBy(ch => ch.Position)
                .Select(ch => MapCategory(ch, locale, baseUrl)).ToList()
        };
    }

    private static ProductResult MapProductFromAttributes(Product p, string baseUrl, ProductService svc)
    {
        var imgPath = p.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path;
        var reviewCount = p.Reviews.Count(r => r.Status == "approved");
        var isSaleable = svc.IsSaleable(p);

        return new ProductResult
        {
            Id = $"/api/shop/products/{p.Id}",
            _Id = p.Id,
            Sku = p.Sku,
            Type = p.Type,
            Name = svc.GetProductName(p),
            UrlKey = svc.GetProductUrlKey(p),
            Price = svc.GetProductPrice(p),
            MinimumPrice = svc.GetEffectivePrice(p),
            SpecialPrice = svc.GetProductSpecialPrice(p),
            BaseImageUrl = imgPath != null ? $"{baseUrl}/storage/{imgPath}" : null,
            IsSaleable = isSaleable,
            Reviews = new Connection<ReviewResult>
            {
                TotalCount = reviewCount,
                Edges = p.Reviews.Where(r => r.Status == "approved").Take(5).Select((r, i) => new Edge<ReviewResult>
                {
                    Node = new ReviewResult
                    {
                        Rating = r.Rating, Id = r.Id, Name = r.Name, Title = r.Title,
                        Comment = r.Comment, CreatedAt = r.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
                    },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(i.ToString()))
                }).ToList(),
                PageInfo = new PageInfo()
            }
        };
    }

    private static ProductDetailResult MapProductDetail(Product p, string baseUrl, string locale, ProductService svc)
    {
        var imgPath = p.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path;

        return new ProductDetailResult
        {
            Id = $"/api/shop/products/{p.Id}",
            _Id = p.Id,
            Sku = p.Sku,
            Type = p.Type,
            Name = svc.GetProductName(p),
            UrlKey = svc.GetProductUrlKey(p),
            Description = svc.GetProductDescription(p),
            ShortDescription = svc.GetProductShortDescription(p),
            Price = svc.GetProductPrice(p),
            MinimumPrice = svc.GetEffectivePrice(p),
            SpecialPrice = svc.GetProductSpecialPrice(p),
            BaseImageUrl = imgPath != null ? $"{baseUrl}/storage/{imgPath}" : null,
            IsSaleable = svc.IsSaleable(p),
            Color = svc.GetAttributeValue(p, "color"),
            Size = svc.GetAttributeValue(p, "size"),
            Brand = svc.GetAttributeValue(p, "brand"),
            Images = new Connection<ImageResult>
            {
                Edges = p.Images.OrderBy(i => i.Position).Select((img, idx) => new Edge<ImageResult>
                {
                    Node = new ImageResult
                    {
                        Id = $"/api/shop/product-images/{img.Id}",
                        _Id = img.Id,
                        Path = img.Path,
                        PublicPath = $"{baseUrl}/storage/{img.Path}",
                        Type = img.Type,
                        Position = img.Position
                    },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(idx.ToString()))
                }).ToList(),
                TotalCount = p.Images.Count,
                PageInfo = new PageInfo()
            },
            SuperAttributes = new Connection<SuperAttributeResult>
            {
                Edges = p.SuperAttributes.Select((sa, idx) => new Edge<SuperAttributeResult>
                {
                    Node = new SuperAttributeResult
                    {
                        Id = sa.Id,
                        Code = sa.Code,
                        AdminName = sa.AdminName,
                        Options = new Connection<SuperAttributeOptionResult>
                        {
                            Edges = sa.Options.Select((o, oi) => new Edge<SuperAttributeOptionResult>
                            {
                                Node = new SuperAttributeOptionResult
                                {
                                    Id = o.Id, _Id = o.Id, AdminName = o.AdminName,
                                    SwatchValue = o.SwatchValue,
                                    Translation = o.Translations.FirstOrDefault(t => t.Locale == locale) is { } tr
                                        ? new LabelResult { Label = tr.Label }
                                        : new LabelResult { Label = o.AdminName }
                                },
                                Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(oi.ToString()))
                            }).ToList(),
                            TotalCount = sa.Options.Count,
                            PageInfo = new PageInfo()
                        }
                    },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(idx.ToString()))
                }).ToList(),
                TotalCount = p.SuperAttributes.Count,
                PageInfo = new PageInfo()
            },
            Variants = new Connection<VariantResult>
            {
                Edges = p.Children.Select((v, idx) =>
                {
                    var vImg = v.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path;
                    return new Edge<VariantResult>
                    {
                        Node = new VariantResult
                        {
                            Id = $"/api/shop/products/{v.Id}",
                            _Id = v.Id,
                            Sku = v.Sku,
                            Name = svc.GetProductName(v),
                            Price = svc.GetProductPrice(v),
                            SpecialPrice = svc.GetProductSpecialPrice(v),
                            BaseImageUrl = vImg != null ? $"{baseUrl}/storage/{vImg}" : imgPath != null ? $"{baseUrl}/storage/{imgPath}" : null,
                            IsSaleable = v.Inventories.Sum(i => i.Qty) > 0 || !v.Inventories.Any(),
                            Color = svc.GetAttributeValue(v, "color"),
                            Size = svc.GetAttributeValue(v, "size")
                        },
                        Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(idx.ToString()))
                    };
                }).ToList(),
                TotalCount = p.Children.Count,
                PageInfo = new PageInfo()
            },
            Reviews = new Connection<ReviewResult>
            {
                Edges = p.Reviews.Where(r => r.Status == "approved").Select((r, i) => new Edge<ReviewResult>
                {
                    Node = new ReviewResult { Rating = r.Rating, Id = r.Id, Name = r.Name, Title = r.Title, Comment = r.Comment, CreatedAt = r.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") },
                    Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(i.ToString()))
                }).ToList(),
                TotalCount = p.Reviews.Count(r => r.Status == "approved"),
                PageInfo = new PageInfo()
            },
            RelatedProducts = new Connection<ProductResult>
            {
                Edges = p.RelatedProducts.Select((rp, i) =>
                {
                    var rpImg = rp.Images.OrderBy(im => im.Position).FirstOrDefault()?.Path;
                    return new Edge<ProductResult>
                    {
                        Node = new ProductResult
                        {
                            Id = $"/api/shop/products/{rp.Id}", _Id = rp.Id, Sku = rp.Sku,
                            Type = rp.Type, Name = svc.GetProductName(rp), UrlKey = svc.GetProductUrlKey(rp),
                            Price = svc.GetProductPrice(rp), MinimumPrice = svc.GetEffectivePrice(rp),
                            SpecialPrice = svc.GetProductSpecialPrice(rp),
                            BaseImageUrl = rpImg != null ? $"{baseUrl}/storage/{rpImg}" : null,
                            IsSaleable = rp.Inventories.Sum(inv => inv.Qty) > 0 || !rp.Inventories.Any()
                        },
                        Cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(i.ToString()))
                    };
                }).ToList(),
                TotalCount = p.RelatedProducts.Count,
                PageInfo = new PageInfo()
            }
        };
    }
}

// ─── Result DTOs matching Flutter's expected GraphQL response shape ─────

public class TreeCategoryResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public int Position { get; set; }
    public string? LogoPath { get; set; }
    public string? LogoUrl { get; set; }
    public string? BannerUrl { get; set; }
    public bool Status { get; set; }
    public CategoryTranslationResult? Translation { get; set; }
    public List<TreeCategoryResult> Children { get; set; } = new();
}

public class CategoryTranslationResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? UrlPath { get; set; }
    public string? MetaTitle { get; set; }
}

// ─── categoryBrowse query — one screen of the nested catalogue ─────────

/// <summary>Everything one category screen needs: the category itself, the
/// ancestor breadcrumb, the direct sub-categories, and the products that sit
/// directly at this level (paginated as a connection).</summary>
public class CategoryBrowseResult
{
    public BrowseCategoryResult Category { get; set; } = new();
    public List<CategoryBriefResult> Breadcrumb { get; set; } = new();
    /// <summary>The store (top-level category) this category belongs to.</summary>
    public CategoryBriefResult Store { get; set; } = new();
    /// <summary>The store's top-level categories — the app's Flipkart-style
    /// tab strip shown on every category screen.</summary>
    public List<CategoryBriefResult> MainCategories { get; set; } = new();
    public List<BrowseSubcategoryResult> Subcategories { get; set; } = new();
    public bool HasSubcategories { get; set; }
    public Connection<ProductResult>? Products { get; set; }
}

public class BrowseCategoryResult
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public int? ParentId { get; set; }
    public string? LogoPath { get; set; }
    public string? LogoUrl { get; set; }
    public string? BannerUrl { get; set; }
}

public class BrowseSubcategoryResult
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? LogoPath { get; set; }
    public string? LogoUrl { get; set; }
    public string? BannerUrl { get; set; }
    /// <summary>True when tapping this tile should drill into another
    /// category screen; false marks a leaf that only has products.</summary>
    public bool HasChildren { get; set; }
    /// <summary>Total distinct products anywhere under this sub-category
    /// (its own level + every descendant).</summary>
    public int ProductCount { get; set; }
}

public class CategoryBriefResult
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    /// <summary>Set for main-category tabs; null for breadcrumb nodes.</summary>
    public string? LogoUrl { get; set; }
}

public class ProductResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Sku { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? UrlKey { get; set; }
    public decimal Price { get; set; }
    public decimal MinimumPrice { get; set; }
    public decimal? SpecialPrice { get; set; }
    public string? BaseImageUrl { get; set; }
    public bool IsSaleable { get; set; }
    public Connection<ReviewResult>? Reviews { get; set; }
}

public class ProductDetailResult : ProductResult
{
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
    public string? Color { get; set; }
    public string? Size { get; set; }
    public string? Brand { get; set; }
    public Connection<ImageResult>? Images { get; set; }
    public Connection<SuperAttributeResult>? SuperAttributes { get; set; }
    public Connection<VariantResult>? Variants { get; set; }
    public new Connection<ReviewResult>? Reviews { get; set; }
    public Connection<ProductResult>? RelatedProducts { get; set; }
}

public class ImageResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Path { get; set; } = "";
    public string? PublicPath { get; set; }
    public string? Type { get; set; }
    public int Position { get; set; }
}

public class SuperAttributeResult
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string AdminName { get; set; } = "";
    public Connection<SuperAttributeOptionResult>? Options { get; set; }
}

public class SuperAttributeOptionResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? AdminName { get; set; }
    public string? SwatchValue { get; set; }
    public string? SwatchValueUrl { get; set; }
    public LabelResult? Translation { get; set; }
}

public class LabelResult { public string? Label { get; set; } }

public class VariantResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Name { get; set; }
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public string? BaseImageUrl { get; set; }
    public bool IsSaleable { get; set; }
    public string? Color { get; set; }
    public string? Size { get; set; }
}

public class ReviewResult
{
    public int Rating { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Comment { get; set; }
    public string? CreatedAt { get; set; }
}

public class ChannelResult
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? Hostname { get; set; }
    public TranslationResult? Translation { get; set; }
}

public class TranslationResult { public int Id { get; set; } public string Name { get; set; } = ""; }

public class ThemeCustomizationResult
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public bool Status { get; set; }
    public int SortOrder { get; set; }
    public Connection<ThemeTranslationResult>? Translations { get; set; }
}

public class ThemeTranslationResult
{
    public int Id { get; set; }
    public int ThemeCustomizationId { get; set; }
    public string Locale { get; set; } = "";
    public string? Options { get; set; }
}

public class AttributeResult
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public Connection<AttributeOptionResult>? Options { get; set; }
}

public class AttributeOptionResult
{
    public int Id { get; set; }
    public string? AdminName { get; set; }
    public Connection<OptionTranslationResult>? Translations { get; set; }
}

public class OptionTranslationResult
{
    public int Id { get; set; }
    public string? Label { get; set; }
    public string? Locale { get; set; }
}

public class CategoryAttributeFilterResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Code { get; set; } = "";
    public string AdminName { get; set; } = "";
    public string Type { get; set; } = "";
    public string? SwatchType { get; set; }
    public string? Validation { get; set; }
    public int? Position { get; set; }
    public bool IsRequired { get; set; }
    public bool IsUnique { get; set; }
    public bool IsFilterable { get; set; }
    public bool IsComparable { get; set; }
    public bool IsConfigurable { get; set; }
    public bool IsUserDefined { get; set; }
    public bool IsVisibleOnFront { get; set; }
    public bool ValuePerLocale { get; set; }
    public bool ValuePerChannel { get; set; }
    public string? DefaultValue { get; set; }
    public decimal? MaxPrice { get; set; }
    public decimal? MinPrice { get; set; }
    public string? Validations { get; set; }
    public List<AttrTransResult> Translations { get; set; } = new();
    public List<AttrOptionFullResult> Options { get; set; } = new();
}

public class AttrTransResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public int AttributeId { get; set; }
    public string Locale { get; set; } = "";
    public string? Name { get; set; }
}

public class AttrOptionFullResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? AdminName { get; set; }
    public int? SortOrder { get; set; }
    public string? SwatchValue { get; set; }
    public string? SwatchValueUrl { get; set; }
    public OptionTransResult? Translation { get; set; }
    public List<OptionTransResult> Translations { get; set; } = new();
}

public class OptionTransResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public int AttributeOptionId { get; set; }
    public string Locale { get; set; } = "";
    public string? Label { get; set; }
}
