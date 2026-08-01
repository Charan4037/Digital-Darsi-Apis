using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;

namespace DOSApi.Services;

/// <summary>
/// Cached attribute IDs so we don't look them up every time.
/// </summary>
public class AttrIds
{
    public int Name;
    public int Price;
    public int UrlKey;
    public int Description;
    public int ShortDescription;
    public int SpecialPrice;
    public int Status;
    public int Sku;
    public bool Loaded;
}

public class ProductService
{
    private readonly DOSDbContext _db;
    private readonly string _baseUrl;
    private readonly string _locale;
    private static readonly AttrIds _attrIds = new();

    public ProductService(DOSDbContext db, IConfiguration config, LocaleContext localeCtx)
    {
        _db = db;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
        _locale = localeCtx.Locale;
    }

    public async Task EnsureAttrIdsAsync()
    {
        if (_attrIds.Loaded) return;
        var attrs = await _db.Attributes
            .Where(a => new[] { "name", "price", "url_key", "description", "short_description", "special_price", "status", "sku" }.Contains(a.Code))
            .Select(a => new { a.Code, a.Id })
            .ToListAsync();
        foreach (var a in attrs)
        {
            switch (a.Code)
            {
                case "name": _attrIds.Name = a.Id; break;
                case "price": _attrIds.Price = a.Id; break;
                case "url_key": _attrIds.UrlKey = a.Id; break;
                case "description": _attrIds.Description = a.Id; break;
                case "short_description": _attrIds.ShortDescription = a.Id; break;
                case "special_price": _attrIds.SpecialPrice = a.Id; break;
                case "status": _attrIds.Status = a.Id; break;
                case "sku": _attrIds.Sku = a.Id; break;
            }
        }
        _attrIds.Loaded = true;
    }

    // ─── Helper: read attribute values for a product ────────────────────

    public string? GetAttrText(Product p, int attrId)
    {
        return p.AttributeValues.FirstOrDefault(v => v.AttributeId == attrId && v.Locale == _locale)?.TextValue
            ?? p.AttributeValues.FirstOrDefault(v => v.AttributeId == attrId && v.Locale == null)?.TextValue;
    }

    public decimal? GetAttrDecimal(Product p, int attrId)
    {
        return p.AttributeValues.FirstOrDefault(v => v.ProductId == p.Id && v.AttributeId == attrId)?.FloatValue;
    }

    /// <summary>
    /// Optimized QueryProductsAsync with reduced split queries and efficient filtering.
    /// </summary>
    public async Task<(List<Product> items, int totalCount)> QueryProductsAsync(
        string? filter, string? sortKey, bool reverse, string? query, int offset, int limit)
    {
        await EnsureAttrIdsAsync();                             

        // Step 1: Build base query with minimal includes (pagination-only)
        var baseQuery = _db.Products
            .AsNoTracking()
            .Where(p => p.ParentId == null && p.Flats.Any(f => f.Status == true));

        // Step 2: Apply filters on database before pagination
        baseQuery = ApplyFilters(baseQuery, filter, query);

        // Step 3: Get total count before pagination
        var totalCount = await baseQuery.CountAsync();

        // Step 4: Apply sorting at database level
        var sortedQuery = ApplySorting(baseQuery, sortKey, reverse);

        // Step 5: Paginate (BEFORE loading heavy relations)
        var paginatedIds = await sortedQuery
            .Select(p => p.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        if (paginatedIds.Count == 0)
            return (new(), totalCount);

        // Step 6: Load detailed data ONLY for paginated results
        var items = await _db.Products
            .AsNoTracking()
            .Where(p => paginatedIds.Contains(p.Id))
            .Include(p => p.AttributeValues)
            .Include(p => p.Flats)
            // No .Take() here — MySQL can't translate a windowed Take() on a
            // split-query collection include (needs ROW_NUMBER, which this
            // server's MySQL version doesn't support). Callers cap Images to
            // a display limit in-memory after materialization instead.
            .Include(p => p.Images.OrderBy(i => i.Position))
            .Include(p => p.Reviews.Where(r => r.Status == "approved"))
            .Include(p => p.Inventories)
            .Include(p => p.Categories)
            .Include(p => p.Children).ThenInclude(c => c.AttributeValues)
            .Include(p => p.Children).ThenInclude(c => c.Flats)
            .Include(p => p.Children).ThenInclude(c => c.Inventories)
            .AsSplitQuery()
            .ToListAsync();

        // Step 7: Restore original sort order (pagination order, not DB order)
        var orderedItems = paginatedIds
            .Select(id => items.First(p => p.Id == id))
            .ToList();

        return (orderedItems, totalCount);
    }

    /// <summary>
    /// Apply filter and search filters at database level.
    /// </summary>
    private IQueryable<Product> ApplyFilters(IQueryable<Product> query, string? filter, string? searchQuery)
    {
        // Parse and apply structured filters
        if (!string.IsNullOrEmpty(filter))
        {
            try
            {
                var filters = JsonSerializer.Deserialize<Dictionary<string, string>>(filter);
                if (filters != null)
                {
                    // Category filter
                    if (filters.TryGetValue("category_id", out var catId) && int.TryParse(catId, out var categoryId))
                    {
                        query = query.Where(p => p.Categories.Any(c => c.Id == categoryId));
                    }

                    // Price filter - use Flats table for performance
                    if (filters.TryGetValue("price", out var priceRange))
                    {
                        var parts = priceRange.Split(',');
                        if (parts.Length == 2 && decimal.TryParse(parts[0], out var minP) && decimal.TryParse(parts[1], out var maxP))
                        {
                            query = query.Where(p =>
                                p.Flats.Any(f => f.Price >= minP && f.Price <= maxP));
                        }
                    }

                    // Name filter - use Flats table (case-insensitive)
                    if (filters.TryGetValue("name", out var nameFilter))
                    {
                        var lowerName = nameFilter.ToLower();
                        query = query.Where(p =>
                            p.Flats.Any(f => f.Name != null && EF.Functions.Like(f.Name, $"%{lowerName}%")));
                    }
                }
            }
            catch { }
        }

        // Search query - use Flats table with case-insensitive search
        if (!string.IsNullOrEmpty(searchQuery))
        {
            var lowerQuery = searchQuery.ToLower();
            query = query.Where(p =>
                p.Flats.Any(f => f.Name != null && EF.Functions.Like(f.Name, $"%{lowerQuery}%")));
        }

        return query;
    }

    /// <summary>
    /// Apply sorting at database level with deterministic tie-breaking.
    /// </summary>
    private IOrderedQueryable<Product> ApplySorting(IQueryable<Product> query, string? sortKey, bool reverse)
    {
        IOrderedQueryable<Product> ordered = sortKey?.ToUpper() switch
        {
            // Sort by price from Flats table
            "PRICE" => reverse
                ? query.OrderByDescending(p => p.Flats.First().Price ?? 0)
                : query.OrderBy(p => p.Flats.First().Price ?? 0),

            // Sort by creation date
            "CREATED_AT" => reverse
                ? query.OrderByDescending(p => p.CreatedAt)
                : query.OrderBy(p => p.CreatedAt),

            // Default: sort by title (from Flats table)
            _ => reverse
                ? query.OrderByDescending(p => p.Flats.First().Name ?? p.Sku)
                : query.OrderBy(p => p.Flats.First().Name ?? p.Sku)
        };

        // Deterministic tie-breaking
        return reverse
            ? ordered.ThenByDescending(p => p.Id)
            : ordered.ThenBy(p => p.Id);
    }

    // ─── Get Product by URL Key (from attribute values) ─────────────────

    public async Task<Product?> GetProductByUrlKeyAsync(string urlKey)
    {
        await EnsureAttrIdsAsync();

        // Find product by URL key, prioritizing parent products over variants
        // (variants and parents can share the same URL key in Magento)
        var matchingFlats = await _db.ProductFlats
            .Where(pf => pf.UrlKey == urlKey && pf.Locale == _locale)
            .Select(pf => pf.ProductId)
            .ToListAsync();

        int productId = 0;
        if (matchingFlats.Any())
        {
            // Load products and prefer parent (ParentId=null) over variants
            var products = await _db.Products
                .Where(p => matchingFlats.Contains(p.Id))
                .ToListAsync();

            var parent = products.FirstOrDefault(p => p.ParentId == null);
            productId = parent?.Id ?? products.First().Id;
        }

        if (productId == 0) return null;

        return await _db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Include(p => p.AttributeValues)
            .Include(p => p.Flats)
            .Include(p => p.Images.OrderBy(i => i.Position))
            .Include(p => p.Reviews.Where(r => r.Status == "approved"))
            .Include(p => p.SuperAttributes).ThenInclude(a => a.Options).ThenInclude(o => o.Translations)
            .Include(p => p.Children).ThenInclude(c => c.AttributeValues)
            .Include(p => p.Children).ThenInclude(c => c.Flats)
            .Include(p => p.Children).ThenInclude(c => c.Images)
            .Include(p => p.Children).ThenInclude(c => c.Inventories)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.AttributeValues)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Flats)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Images)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Reviews)
            .Include(p => p.Inventories)
            .FirstOrDefaultAsync();
    }

    // ─── Convenience methods: read attribute_values first, fall back to product_flat ─

    public string? GetProductName(Product p)
    {
        return GetAttrText(p, _attrIds.Name) ?? GetFlat(p)?.Name ?? p.Sku;
    }

    public decimal GetProductPrice(Product p)
    {
        return GetAttrDecimal(p, _attrIds.Price) ?? GetFlat(p)?.Price ?? 0;
    }

    public decimal? GetProductSpecialPrice(Product p)
    {
        return GetAttrDecimal(p, _attrIds.SpecialPrice) ?? GetFlat(p)?.SpecialPrice;
    }

    public string? GetProductUrlKey(Product p)
    {
        return GetAttrText(p, _attrIds.UrlKey) ?? GetFlat(p)?.UrlKey;
    }

    public string? GetProductDescription(Product p)
    {
        return GetAttrText(p, _attrIds.Description) ?? GetFlat(p)?.Description;
    }

    public string? GetProductShortDescription(Product p)
    {
        return GetAttrText(p, _attrIds.ShortDescription) ?? GetFlat(p)?.ShortDescription;
    }

    /// <summary>
    /// Vendor name stored in Product.Additional (set by DigitalDarsiSeeder),
    /// picked for the current request locale (vendor_en / vendor_te) — the
    /// source site renders this differently per locale just like name/
    /// description. Falls back to the other locale if the requested one is
    /// blank. Returns null if the product has no vendor info at all.
    /// </summary>
    public string? GetProductVendor(Product p) => ExtractVendorName(p.Additional, _locale);

    /// <summary>
    /// Pulls the free-text seller name out of a product's `additional` JSON
    /// (imported from scraped source sites — key `vendor_en`/`vendor_te`,
    /// no relational link to any account). Static and takes the raw JSON
    /// string directly so callers can extract vendor names in bulk (e.g.
    /// VendorCatalogSeeder, AdminVendorsController) without materializing
    /// full Product entities.
    /// </summary>
    public static string? ExtractVendorName(string? additionalJson, string locale)
    {
        if (string.IsNullOrEmpty(additionalJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(additionalJson);
            var key = string.Equals(locale, "te", StringComparison.OrdinalIgnoreCase) ? "vendor_te" : "vendor_en";
            var fallbackKey = key == "vendor_te" ? "vendor_en" : "vendor_te";
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
            if (doc.RootElement.TryGetProperty(fallbackKey, out var f) && f.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(f.GetString()))
                return f.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>One row of the product specification table.</summary>
    public record ProductSpecInfo(string Name, string Value);

    /// <summary>
    /// Product specification table (Brand, Weight, Shelf Life, …) scraped from
    /// the source site and stored as a key/value object in
    /// <see cref="Product.Additional"/> by <c>DigitalDarsiSeeder</c>. Returned
    /// as an ordered list so the storefront renders rows in a stable sequence.
    /// Empty when the product carries no specs.
    /// </summary>
    public List<ProductSpecInfo> GetProductSpecs(Product p)
    {
        var result = new List<ProductSpecInfo>();
        if (string.IsNullOrEmpty(p.Additional)) return result;
        try
        {
            using var doc = JsonDocument.Parse(p.Additional);
            if (!doc.RootElement.TryGetProperty("specs", out var specs) ||
                specs.ValueKind != JsonValueKind.Object)
                return result;

            // Support locale-keyed format: { "te": {...}, "en": {...} }
            // Fall back to the other locale if the requested one is absent.
            JsonElement flat;
            if (specs.TryGetProperty(_locale, out var localeSpecs) && localeSpecs.ValueKind == JsonValueKind.Object)
                flat = localeSpecs;
            else if (specs.TryGetProperty(_locale == "te" ? "en" : "te", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
                flat = fallback;
            else
                flat = specs; // legacy flat format

            foreach (var prop in flat.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var val = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(prop.Name) && !string.IsNullOrWhiteSpace(val))
                    result.Add(new ProductSpecInfo(prop.Name, val!));
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>
    /// One purchasable variation row, enriched so the storefront can render a
    /// selectable chip and add the right thing to the cart.
    ///
    /// Two ways a product can have variations:
    ///   1. Configurable DOS products → real child products under
    ///      <see cref="Product.Children"/>, each with their own price/stock.
    ///   2. Scraped Digital Darsi products → descriptive metadata in
    ///      <see cref="Product.Additional"/> (no child rows). The user picks a
    ///      weight chip but the cart line points at the parent product, since
    ///      that's the only purchasable row we have on disk today.
    /// </summary>
    public record ProductVariationInfo(
        string Label,
        string Value,
        int ProductId,
        decimal? Price,
        decimal? SpecialPrice,
        string? FormattedPrice,
        bool InStock,
        int MinQty = 1,
        int? MaxQty = null);

    /// <summary>Translates Telugu attribute labels to English when the client
    /// requested the English locale. The scraper captured labels from a Telugu
    /// source page so they ship in Telugu regardless of <c>Accept-Language</c>;
    /// this dictionary closes that gap without requiring a re-scrape.</summary>
    private static readonly Dictionary<string, string> TeluguLabelToEnglish =
        new(StringComparer.Ordinal)
        {
            ["గ్రాములు"] = "Grams",
            ["కిలోలు"] = "Kilograms",
            ["కిలో"] = "Kilogram",
            ["ముక్కలు"] = "Pieces",
            ["సంఖ్య"] = "Quantity",
            ["పరిమాణం"] = "Size",
            ["రంగు"] = "Color",
            // The generic "please choose" prompt some products carry as the
            // attribute's own <dt> label (not just as a placeholder option
            // value — see VariationPlaceholderValues below, a different
            // Telugu spelling used in that DOM position) when the source
            // site didn't set a real per-attribute name. Falls back to the
            // same generic term used elsewhere when no label exists at all.
            ["దయచేసి ఎంచుకోండి"] = "Variant",
        };

    /// <summary>Source-site placeholder dropdown values that should never be
    /// rendered as a selectable chip. "దయచేసి ఎన్నుకోండి" is the literal
    /// "Please select" prompt the scraper picks up alongside real options.</summary>
    private static readonly HashSet<string> VariationPlaceholderValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "దయచేసి ఎన్నుకోండి",
            "Please select",
            "Select",
            "Choose",
            "--",
        };

    /// <summary>
    /// Returns the variation rows the storefront should render. Reads from
    /// <see cref="Product.Children"/> when the product is configurable
    /// (preferred — real per-variant pricing/stock), and falls back to the
    /// scraped key/value metadata stored in <see cref="Product.Additional"/>
    /// otherwise. Returns an empty list when the product has neither.
    /// </summary>
    public List<ProductVariationInfo> GetProductVariations(Product p)
    {
        // 1) Real configurable variants — each child is its own purchasable
        //    DOS product with a price + inventory row. This is the path
        //    we'd hit for products seeded by DigitalDarsiSeeder (which writes
        //    variant_value / variant_label into the child's Additional JSON)
        //    or imported via DOS's admin (where p.SuperAttributes drives
        //    the axis label and child names carry the option text).
        if (p.Children != null && p.Children.Count > 0)
        {
            var rows = new List<ProductVariationInfo>(p.Children.Count);
            // Sort by the position the seeder remembered so the chip order
            // matches the source site (50g, 100g, …, 1kg). Falls through to
            // child id for non-seeder data, which preserves insertion order.
            var ordered = p.Children
                .Select(c => (Child: c, Extras: ReadChildVariantExtras(c)))
                .OrderBy(t => t.Extras.Position)
                .ThenBy(t => t.Child.Id)
                .ToList();

            foreach (var (child, extras) in ordered)
            {
                var childPrice = GetProductPrice(child);
                var childSpecial = GetProductSpecialPrice(child);
                var childInStock = IsSaleable(child);

                // Prefer the seeder-stamped variant value (clean: "50g") over
                // the child's full name ("Bangala dumpa - 50g") so the chip
                // stays terse. Fall back to derived options only when the
                // metadata isn't there (admin-imported products).
                var value = !string.IsNullOrEmpty(extras.Value)
                    ? extras.Value
                    : DeriveValueFromChildName(GetProductName(child), GetProductName(p));
                if (string.IsNullOrEmpty(value)) value = child.Sku;

                var rawLabel = !string.IsNullOrEmpty(extras.Label)
                    ? extras.Label
                    : (p.SuperAttributes.FirstOrDefault()?.AdminName ?? "Variant");
                var label = TranslateLabel(rawLabel);

                var effective = (childSpecial.HasValue && childSpecial > 0)
                    ? childSpecial.Value
                    : childPrice;

                var childFlat = GetFlat(child);
                rows.Add(new ProductVariationInfo(
                    Label: label,
                    Value: value,
                    ProductId: child.Id,
                    Price: childPrice,
                    SpecialPrice: childSpecial,
                    FormattedPrice: $"₹{effective:N2}",
                    InStock: childInStock,
                    MinQty: childFlat?.MinQty ?? 1,
                    MaxQty: childFlat?.MaxQty));
            }
            return rows;
        }

        // 2) Descriptive metadata from the scraper. No child products on disk
        //    so every chip's add-to-cart routes to the parent — but we still
        //    parse out per-option pricing where the source site embedded it
        //    (e.g. "[500g -₹17.00 ]" → value="500g", price=17.00).
        if (string.IsNullOrEmpty(p.Additional)) return new List<ProductVariationInfo>();

        var list = new List<ProductVariationInfo>();
        try
        {
            using var doc = JsonDocument.Parse(p.Additional);
            if (!doc.RootElement.TryGetProperty("variations", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            var parentPrice = GetProductPrice(p);
            var parentSpecial = GetProductSpecialPrice(p);
            var parentInStock = IsSaleable(p);

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("Key", out var k) ||
                    !el.TryGetProperty("Value", out var val)) continue;

                var rawLabel = k.GetString() ?? "";
                var rawValue = (val.GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(rawValue)) continue;
                if (VariationPlaceholderValues.Contains(rawValue)) continue;

                var (cleanedValue, embeddedPrice) = ParseEmbeddedPrice(rawValue);
                if (string.IsNullOrEmpty(cleanedValue)) continue;

                var price = embeddedPrice ?? parentPrice;
                var special = embeddedPrice.HasValue ? null : parentSpecial;
                var effective = (special.HasValue && special > 0) ? special.Value : price;

                list.Add(new ProductVariationInfo(
                    Label: TranslateLabel(rawLabel),
                    Value: cleanedValue,
                    ProductId: p.Id,
                    Price: price,
                    SpecialPrice: special,
                    FormattedPrice: $"₹{effective:N2}",
                    InStock: parentInStock));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Walks the whole sub-tree of <paramref name="id"/> in memory
    /// (active categories only). Loads only (id, parent_id) — one lightweight
    /// query, no MySQL version requirement (works on MySQL 5.1). Shared by
    /// CategoryController's storefront browse and the admin product-list
    /// endpoints' "category + all its subcategories" filter, so both stay
    /// consistent about what "in this category" means.</summary>
    public async Task<List<int>> GetSubtreeCategoryIdsAsync(int id)
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

    /// <summary>
    /// Resolves which product row list/browse cards should read price and
    /// stock from. For a simple product this is just <paramref name="p"/>
    /// itself. For a product with real child variants, the parent's own
    /// product_flat price is often stale/unused — the storefront's
    /// product-details screen always displays whichever variant it
    /// auto-selects (first in-stock child, else the first child, using the
    /// same ordering as <see cref="GetProductVariations"/>), so list cards
    /// must resolve through the same child or they'll show a different price
    /// than the detail page for the same product.
    /// </summary>
    public Product GetPricingProduct(Product p)
    {
        if (p.Children == null || p.Children.Count == 0) return p;

        var ordered = p.Children
            .Select(c => (Child: c, Extras: ReadChildVariantExtras(c)))
            .OrderBy(t => t.Extras.Position)
            .ThenBy(t => t.Child.Id)
            .ToList();

        var firstInStock = ordered.FirstOrDefault(t => IsSaleable(t.Child)).Child;
        return firstInStock ?? ordered[0].Child;
    }

    /// <summary>Translates a non-English attribute label to English when the
    /// active request locale is <c>en</c>. Returns the label unchanged for
    /// other locales or for labels we don't have a translation for.</summary>
    private string TranslateLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        if (!string.Equals(_locale, "en", StringComparison.OrdinalIgnoreCase)) return label;
        return TeluguLabelToEnglish.TryGetValue(label, out var translated)
            ? translated
            : label;
    }

public record ChildVariantExtras(string Value, string Label, int Position);

    /// <summary>Reads the per-variant metadata (chip text, axis label,
    /// position) the seeder stamps into each child's <c>Additional</c> JSON.
    /// Returns empty strings + position 0 for products that didn't go through
    /// our seeder — callers fall through to deriving the chip text from the
    /// child name in that case. Public so admin variant management (which
    /// needs the same value/label off a child row) can reuse it instead of
    /// re-parsing the JSON with slightly different logic.</summary>
    public static ChildVariantExtras ReadChildVariantExtras(Product child)
    {
        if (string.IsNullOrEmpty(child.Additional))
            return new ChildVariantExtras("", "", 0);
        try
        {
            using var doc = JsonDocument.Parse(child.Additional);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ChildVariantExtras("", "", 0);

            string val = "";
            string lab = "";
            int posn = 0;
            if (root.TryGetProperty("variant_value", out var v) && v.ValueKind == JsonValueKind.String)
                val = v.GetString() ?? "";
            if (root.TryGetProperty("variant_label", out var l) && l.ValueKind == JsonValueKind.String)
                lab = l.GetString() ?? "";
            if (root.TryGetProperty("variant_position", out var pp) && pp.ValueKind == JsonValueKind.Number)
                pp.TryGetInt32(out posn);
            return new ChildVariantExtras(val, lab, posn);
        }
        catch (JsonException)
        {
            return new ChildVariantExtras("", "", 0);
        }
    }

    /// <summary>Best-effort fallback when a child product wasn't seeded with
    /// our metadata: strip the parent name prefix from the child's name to
    /// recover the option text. <c>"Bangala dumpa - 50g"</c> →
    /// <c>"50g"</c>.</summary>
    private static string DeriveValueFromChildName(string? childName, string? parentName)
    {
        if (string.IsNullOrEmpty(childName)) return "";
        if (!string.IsNullOrEmpty(parentName) &&
            childName.StartsWith(parentName, StringComparison.Ordinal))
        {
            var tail = childName[parentName.Length..].TrimStart(' ', '-', '–', '—', ':');
            if (!string.IsNullOrEmpty(tail)) return tail;
        }
        return childName;
    }

    private static readonly System.Text.RegularExpressions.Regex EmbeddedPriceRegex =
        new(@"^\[\s*(?<value>.+?)\s*-\s*[₹Rs.\s]*(?<price>\d+(?:\.\d+)?)\s*\]$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Extracts an option name and its embedded price from values
    /// the scraper captured in the <c>[label -₹price]</c> pattern (e.g.
    /// <c>"[500g -₹17.00 ]"</c> → <c>("500g", 17.00m)</c>). Returns the input
    /// unchanged with a null price if it doesn't match the pattern.</summary>
    private static (string Value, decimal? Price) ParseEmbeddedPrice(string raw)
    {
        var match = EmbeddedPriceRegex.Match(raw);
        if (!match.Success) return (raw, null);
        var value = match.Groups["value"].Value.Trim();
        if (decimal.TryParse(match.Groups["price"].Value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var price))
        {
            return (value, price);
        }
        return (value, null);
    }

    public decimal GetEffectivePrice(Product p)
    {
        var special = GetProductSpecialPrice(p);
        if (special.HasValue && special > 0)
            return special.Value;
        return GetProductPrice(p);
    }

    public string? GetBaseImageUrl(Product product)
    {
        var img = product.Images.OrderBy(i => i.Position).FirstOrDefault();
        if (img == null) return null;
        return ResolveImagePath(img.Path);
    }

    public string? GetImagePublicPath(ProductImage img)
    {
        return ResolveImagePath(img.Path);
    }

    /// <summary>Resolves a raw image path to a full URL without needing a loaded
    /// ProductImage entity — for callers that only projected the path column
    /// out of the database (see AdminGlobalProductsController.List) instead of
    /// eager-loading the whole Images collection just to read one row's path.</summary>
    public string? GetImageUrl(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return ResolveImagePath(path);
    }

    private string ResolveImagePath(string path)
    {
        // If already a full URL, return as-is
        if (path.StartsWith("http://") || path.StartsWith("https://"))
            return path;
        return $"{_baseUrl}/storage/{path}";
    }

    public bool IsSaleable(Product product)
    {
        if (product.Inventories.Any())
            return product.Inventories.Sum(i => i.Qty) > 0;
        return true;
    }

    public string? GetAttributeValue(Product product, string code)
    {
        var attrId = _db.Attributes.Where(a => a.Code == code).Select(a => a.Id).FirstOrDefault();
        if (attrId == 0) return null;

        var val = product.AttributeValues.FirstOrDefault(v => v.AttributeId == attrId);
        if (val == null) return null;

        if (val.IntegerValue.HasValue)
        {
            var option = _db.AttributeOptions
                .Include(o => o.Translations)
                .FirstOrDefault(o => o.Id == val.IntegerValue.Value);
            if (option != null)
            {
                var trans = option.Translations.FirstOrDefault(t => t.Locale == _locale);
                return trans?.Label ?? option.AdminName;
            }
        }

        return val.TextValue ?? val.IntegerValue?.ToString() ?? val.FloatValue?.ToString();
    }

    // Legacy methods for backward compat with product_flat
    public ProductFlat? GetFlat(Product product)
    {
        // Get flat for THIS product specifically, not a variant's flat
        // This is important when product.Flats contains flats for both parent and child products
        return product.Flats.FirstOrDefault(f => f.ProductId == product.Id && f.Locale == _locale)
            ?? product.Flats.FirstOrDefault(f => f.ProductId == product.Id)
            ?? product.Flats.FirstOrDefault(f => f.Locale == _locale)
            ?? product.Flats.FirstOrDefault();
    }

    public decimal GetEffectivePrice(ProductFlat flat)
    {
        if (flat.SpecialPrice.HasValue && flat.SpecialPrice > 0)
            return flat.SpecialPrice.Value;
        return flat.Price ?? 0;
    }
}
