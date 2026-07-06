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
        return p.AttributeValues.FirstOrDefault(v => v.AttributeId == attrId)?.FloatValue;
    }

    // ─── Query Products (reads from product_attribute_values) ───────────

    public async Task<(List<Product> items, int totalCount)> QueryProductsAsync(
        string? filter, string? sortKey, bool reverse, string? query, int offset, int limit)
    {
        await EnsureAttrIdsAsync();

        // Exclude child variant products (parent_id != null) — they're not
        // browsable on their own; the configurable parent is the catalog face.
        var q = _db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Where(p => p.ParentId == null)
            .Include(p => p.AttributeValues)
            .Include(p => p.Flats)
            .Include(p => p.Images.OrderBy(i => i.Position))
            .Include(p => p.Reviews.Where(r => r.Status == "approved"))
            .Include(p => p.Inventories)
            .Include(p => p.Categories)
            .AsQueryable();

        // Parse filter JSON
        if (!string.IsNullOrEmpty(filter))
        {
            try
            {
                var filters = JsonSerializer.Deserialize<Dictionary<string, string>>(filter);
                if (filters != null)
                {
                    if (filters.TryGetValue("category_id", out var catId) && int.TryParse(catId, out var categoryId))
                    {
                        q = q.Where(p => p.Categories.Any(c => c.Id == categoryId));
                    }

                    if (filters.TryGetValue("price", out var priceRange))
                    {
                        var parts = priceRange.Split(',');
                        if (parts.Length == 2 && decimal.TryParse(parts[0], out var minP) && decimal.TryParse(parts[1], out var maxP))
                        {
                            q = q.Where(p =>
                                p.AttributeValues.Any(v => v.AttributeId == _attrIds.Price && v.FloatValue >= minP && v.FloatValue <= maxP) ||
                                p.Flats.Any(f => f.Price != null && f.Price >= minP && f.Price <= maxP));
                        }
                    }

                    if (filters.TryGetValue("name", out var nameFilter))
                    {
                        q = q.Where(p =>
                            p.AttributeValues.Any(v => v.AttributeId == _attrIds.Name && v.TextValue != null && v.TextValue.Contains(nameFilter)) ||
                            p.Flats.Any(f => f.Name != null && f.Name.Contains(nameFilter)));
                    }
                }
            }
            catch { }
        }

        // Search query
        if (!string.IsNullOrEmpty(query))
        {
            q = q.Where(p =>
                p.AttributeValues.Any(v => v.AttributeId == _attrIds.Name && v.TextValue != null && v.TextValue.Contains(query)) ||
                p.Flats.Any(f => f.Name != null && f.Name.Contains(query)));
        }

        var totalCount = await q.CountAsync();

        // Sort — we need to join to attribute values for sorting
        switch (sortKey?.ToUpper())
        {
            case "PRICE":
                q = reverse
                    ? q.OrderByDescending(p => p.AttributeValues.Where(v => v.AttributeId == _attrIds.Price).Select(v => v.FloatValue).FirstOrDefault())
                    : q.OrderBy(p => p.AttributeValues.Where(v => v.AttributeId == _attrIds.Price).Select(v => v.FloatValue).FirstOrDefault());
                break;
            case "CREATED_AT":
                q = reverse ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt);
                break;
            default: // TITLE
                q = reverse
                    ? q.OrderByDescending(p =>
                        p.AttributeValues.Where(v => v.AttributeId == _attrIds.Name && v.Locale == _locale).Select(v => v.TextValue).FirstOrDefault()
                        ?? p.AttributeValues.Where(v => v.AttributeId == _attrIds.Name && v.Locale == null).Select(v => v.TextValue).FirstOrDefault())
                    : q.OrderBy(p =>
                        p.AttributeValues.Where(v => v.AttributeId == _attrIds.Name && v.Locale == _locale).Select(v => v.TextValue).FirstOrDefault()
                        ?? p.AttributeValues.Where(v => v.AttributeId == _attrIds.Name && v.Locale == null).Select(v => v.TextValue).FirstOrDefault());
                break;
        }

        var items = await q.Skip(offset).Take(limit).ToListAsync();
        return (items, totalCount);
    }

    // ─── Get Product by URL Key (from attribute values) ─────────────────

    public async Task<Product?> GetProductByUrlKeyAsync(string urlKey)
    {
        await EnsureAttrIdsAsync();

        // Find product ID by url_key attribute
        var productId = await _db.ProductAttributeValues
            .Where(v => v.AttributeId == _attrIds.UrlKey && v.TextValue == urlKey)
            .Select(v => v.ProductId)
            .FirstOrDefaultAsync();

        if (productId == 0)
        {
            // Also try product_flat as fallback
            var flat = await _db.ProductFlats
                .Where(pf => pf.UrlKey == urlKey && pf.Locale == _locale)
                .FirstOrDefaultAsync();
            if (flat != null) productId = flat.ProductId;
        }

        if (productId == 0) return null;

        return await _db.Products
            .AsNoTracking()
            .AsSplitQuery()
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
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Inventories)
            .Include(p => p.Inventories)
            .FirstOrDefaultAsync(p => p.Id == productId);
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
    /// Vendor name stored in Product.Additional (set by DigitalDarsiSeeder).
    /// Returns null if the product has no vendor info.
    /// </summary>
    public string? GetProductVendor(Product p)
    {
        if (string.IsNullOrEmpty(p.Additional)) return null;
        try
        {
            using var doc = JsonDocument.Parse(p.Additional);
            if (doc.RootElement.TryGetProperty("vendor", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
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
        bool InStock);

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

                rows.Add(new ProductVariationInfo(
                    Label: label,
                    Value: value,
                    ProductId: child.Id,
                    Price: childPrice,
                    SpecialPrice: childSpecial,
                    FormattedPrice: $"₹{effective:N2}",
                    InStock: childInStock));
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

    private record ChildVariantExtras(string Value, string Label, int Position);

    /// <summary>Reads the per-variant metadata (chip text, axis label,
    /// position) the seeder stamps into each child's <c>Additional</c> JSON.
    /// Returns empty strings + position 0 for products that didn't go through
    /// our seeder — callers fall through to deriving the chip text from the
    /// child name in that case.</summary>
    private static ChildVariantExtras ReadChildVariantExtras(Product child)
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
        return product.Flats.FirstOrDefault(f => f.Locale == _locale)
            ?? product.Flats.FirstOrDefault();
    }

    public decimal GetEffectivePrice(ProductFlat flat)
    {
        if (flat.SpecialPrice.HasValue && flat.SpecialPrice > 0)
            return flat.SpecialPrice.Value;
        return flat.Price ?? 0;
    }
}
