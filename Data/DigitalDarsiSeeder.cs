using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BagistoApi.Models.Catalog;
using Microsoft.EntityFrameworkCore;

namespace BagistoApi.Data;

/// <summary>
/// Seeds Digital Darsi catalog data from scraped_data.json (produced by
/// the BagistoScraper project). Data covers the four source sites:
/// buildstore, foodstore, store, services. Each site becomes a top-level
/// category; sub-categories inferred from product breadcrumbs. Vendor
/// name is stored in Product.Additional as JSON.
/// </summary>
public static class DigitalDarsiSeeder
{
    private const string ScrapedDataFileName = "scraped_data.json";
    private const string SentinelAdditionalMarker = "dd_seeder_v3";
    // Every Digital Darsi seeder generation stamps an Additional value that
    // starts with this prefix. A re-seed wipes anything carrying the prefix,
    // so switching from the JSON source (v2) to the staging-table source
    // (v3) cleanly replaces the old catalogue instead of duplicating it.
    private const string SentinelPrefix = "dd_seeder";

    private static readonly Dictionary<string, (string En, string Te)> SiteNames = new()
    {
        ["buildstore"] = ("Build Store", "బిల్డ్ స్టోర్"),
        ["foodstore"]  = ("Food Store",  "ఫుడ్ స్టోర్"),
        ["store"]      = ("General Store", "జనరల్ స్టోర్"),
        ["services"]   = ("Services",    "సేవలు"),
    };

    // ─── DTOs matching scraped_data.json ─────────────────────────────────

    private class ScrapedRoot
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public List<ScrapedCategoryDto> Categories { get; set; } = new();
        public List<ScrapedProductDto> Products { get; set; } = new();
    }

    private class ScrapedCategoryDto
    {
        public string SiteKey { get; set; } = "";
        public string Url { get; set; } = "";
        public string Slug { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string NameTe { get; set; } = "";
        public string? DescriptionEn { get; set; }
        public string? DescriptionTe { get; set; }
        public string? Image { get; set; }
        public string? ParentSlug { get; set; }
    }

    private class ScrapedProductDto
    {
        public string SiteKey { get; set; } = "";
        public string Url { get; set; } = "";
        public int SourceProductId { get; set; }
        public string Sku { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string NameTe { get; set; } = "";
        public string? ShortDescriptionEn { get; set; }
        public string? ShortDescriptionTe { get; set; }
        public string? FullDescriptionEn { get; set; }
        public string? FullDescriptionTe { get; set; }
        public string? Vendor { get; set; }
        public decimal Price { get; set; }
        public decimal? OldPrice { get; set; }
        public List<string> Images { get; set; } = new();
        public List<ScrapedBreadcrumbDto> Breadcrumbs { get; set; } = new();
        public List<ScrapedVariationDto> Variations { get; set; } = new();
        // Product specification table — dynamic key/value pairs scraped from
        // the source site. Populated by the staging-table loader; null when
        // seeding from the older JSON file which didn't carry specs.
        public Dictionary<string, string>? Specs { get; set; }
    }

    private class ScrapedBreadcrumbDto
    {
        public string NameEn { get; set; } = "";
        public string NameTe { get; set; } = "";
        public string Href { get; set; } = "";
    }

    private class ScrapedVariationDto
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private class ProductAdditional
    {
        [JsonPropertyName("seeder")]
        public string Seeder { get; set; } = SentinelAdditionalMarker;
        [JsonPropertyName("site")]
        public string Site { get; set; } = "";
        [JsonPropertyName("vendor")]
        public string? Vendor { get; set; }
        [JsonPropertyName("source_url")]
        public string? SourceUrl { get; set; }
        [JsonPropertyName("source_id")]
        public int? SourceId { get; set; }
        [JsonPropertyName("variations")]
        public List<KeyValuePair<string, string>>? Variations { get; set; }
        // The scraped specification table (e.g. Brand, Weight, Shelf Life).
        // Stored in Additional rather than the Bagisto attribute system so
        // the API can surface it without schema changes.
        [JsonPropertyName("specs")]
        public Dictionary<string, string>? Specs { get; set; }
    }

    /// <summary>Extra fields stamped into a child variant's
    /// <see cref="Product.Additional"/> so the API layer can render the
    /// chip without re-deriving anything from the child's name/sku.</summary>
    private class ChildVariantAdditional
    {
        [JsonPropertyName("seeder")]
        public string Seeder { get; set; } = SentinelAdditionalMarker;
        [JsonPropertyName("site")]
        public string Site { get; set; } = "";
        [JsonPropertyName("parent_sku")]
        public string ParentSku { get; set; } = "";
        [JsonPropertyName("variant_value")]
        public string VariantValue { get; set; } = "";
        [JsonPropertyName("variant_label")]
        public string VariantLabel { get; set; } = "";
        [JsonPropertyName("variant_position")]
        public int VariantPosition { get; set; }
        [JsonPropertyName("is_variant")]
        public bool IsVariant { get; set; } = true;
    }

    // ─── Variation cleaning ──────────────────────────────────────────────
    //
    // The scraped JSON dropdown options are noisy: they include the dropdown's
    // "Please select" prompt as if it were a real option, embed per-variant
    // surcharges into the option label using a "[VALUE +/-₹AMOUNT]" pattern,
    // and sometimes use the prompt text as the label too. We normalize all
    // of that here so the seeder gets back a clean list ready to materialize
    // as child products.

    /// <summary>Dropdown headers like "Please select" that the scraper grabs
    /// alongside real options. Compared case-insensitively against the value
    /// (and the label, since some pages bind the prompt to both).</summary>
    private static readonly HashSet<string> VariationPlaceholders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "దయచేసి ఎన్నుకోండి",
            "దయచేసి ఎంచుకోండి",
            "ఎంచుకోండి",
            "బరువును ఎంచుకోండి",
            "పరిమాణాన్ని ఎంచుకోండి:",
            "Please select",
            "select",
            "Select",
            "choose",
            "Choose",
            "choose weight",
            "Choose Weight",
            "Select a Size:",
            "--",
            "---",
        };

    /// <summary>Matches the scraper's bracket pattern, e.g.
    /// <c>"[8mm +₹128.00 ]"</c> or <c>"[500g -₹17.00 ]"</c>. The sign carries
    /// meaning: <c>+</c> is a surcharge over the parent's price and <c>-</c>
    /// is a discount.</summary>
    private static readonly Regex VariantBracketPattern = new(
        @"^\s*\[\s*(?<value>.+?)\s*(?<sign>[+\-])\s*[₹Rs.\s]*(?<amount>[\d,]+(?:\.\d+)?)\s*\]\s*$",
        RegexOptions.Compiled);

    /// <summary>Matches a weight token like "50g", "1kg", "1.5kg".</summary>
    private static readonly Regex WeightPattern = new(
        @"(?<num>\d+(?:\.\d+)?)\s*(?<unit>kg|g)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private record CleanedVariation(
        string Label,
        string Value,
        decimal? PriceDelta,
        int Position);

    /// <summary>Normalizes the raw scraped variation list: strips dropdown
    /// prompts, parses bracket-style price deltas out of the value, and
    /// dedupes. Returns an empty list when nothing real survives — that's
    /// the signal to leave the parent as a simple product.</summary>
    private static List<CleanedVariation> CleanVariations(List<ScrapedVariationDto>? raw)
    {
        var result = new List<CleanedVariation>();
        if (raw == null || raw.Count == 0) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;

        foreach (var v in raw)
        {
            var rawValue = (v.Value ?? "").Trim();
            if (string.IsNullOrEmpty(rawValue)) continue;

            decimal? delta = null;
            var match = VariantBracketPattern.Match(rawValue);
            if (match.Success)
            {
                rawValue = match.Groups["value"].Value.Trim();
                var amountStr = match.Groups["amount"].Value.Replace(",", "");
                if (decimal.TryParse(amountStr, NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var amount))
                {
                    delta = match.Groups["sign"].Value == "-" ? -amount : amount;
                }
            }

            if (string.IsNullOrEmpty(rawValue)) continue;
            if (VariationPlaceholders.Contains(rawValue)) continue;
            if (!seen.Add(rawValue)) continue;

            var rawLabel = (v.Label ?? "").Trim();
            // Some pages render the "Please select" text as both prompt AND
            // <dt> label — keep the variation but drop the noisy label so the
            // API can substitute a sensible default ("Variant").
            if (VariationPlaceholders.Contains(rawLabel)) rawLabel = "";

            result.Add(new CleanedVariation(
                Label: rawLabel,
                Value: rawValue,
                PriceDelta: delta,
                Position: pos++));
        }

        return result;
    }

    /// <summary>Parses a weight token to grams (e.g. <c>"50g"</c> → 50,
    /// <c>"1.5kg"</c> → 1500). Returns null when the input doesn't carry a
    /// weight unit we recognize.</summary>
    private static decimal? ParseWeightInGrams(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = WeightPattern.Match(text);
        if (!match.Success) return null;
        if (!decimal.TryParse(match.Groups["num"].Value,
                NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
        {
            return null;
        }
        return match.Groups["unit"].Value.Equals("kg", StringComparison.OrdinalIgnoreCase)
            ? num * 1000m
            : num;
    }

    /// <summary>Rounds a computed price to a sensible currency precision and
    /// prevents negatives. Used both for prorated weight pricing and for
    /// applying scraped <c>-₹X</c> deltas where the math could underflow.</summary>
    private static decimal SanitizePrice(decimal value)
    {
        if (value < 0) return 0;
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Decides what a variant should cost relative to the parent.
    /// Resolution order:
    /// <list type="number">
    ///   <item>If the scraped value carried an explicit delta in brackets,
    ///         apply <c>parent.Price ± delta</c> and treat that as the variant's
    ///         regular + special prices proportionally.</item>
    ///   <item>Else, if both parent and variant carry a weight unit, prorate
    ///         the parent price by the weight ratio (a 50g chip on a 1kg
    ///         parent priced at ₹29 → 50/1000 × ₹29 = ₹1.45).</item>
    ///   <item>Else, fall back to the parent price unchanged. Non-prorate-able
    ///         options like color/size codes default to the same price.</item>
    /// </list>
    /// </summary>
    private static (decimal Price, decimal? OldPrice) ComputeVariantPrice(
        ScrapedProductDto parent,
        CleanedVariation cv)
    {
        var parentPrice = parent.Price;
        var parentOld = parent.OldPrice;

        // 1) Explicit delta from scraped bracket pattern
        if (cv.PriceDelta.HasValue)
        {
            var newPrice = SanitizePrice(parentPrice + cv.PriceDelta.Value);
            decimal? newOld = parentOld.HasValue
                ? SanitizePrice(parentOld.Value + cv.PriceDelta.Value)
                : null;
            // Clamp: if the discount erased any "old price" gap, drop it so we
            // don't render a misleading strike-through on the variant.
            if (newOld.HasValue && newOld.Value <= newPrice) newOld = null;
            return (newPrice, newOld);
        }

        // 2) Prorate by weight when both sides carry a weight unit
        var variantGrams = ParseWeightInGrams(cv.Value);
        var parentGrams = ParseWeightInGrams(parent.NameEn) ?? ParseWeightInGrams(parent.NameTe);
        if (variantGrams.HasValue && parentGrams.HasValue && parentGrams.Value > 0)
        {
            var ratio = variantGrams.Value / parentGrams.Value;
            var newPrice = SanitizePrice(parentPrice * ratio);
            decimal? newOld = parentOld.HasValue
                ? SanitizePrice(parentOld.Value * ratio)
                : null;
            if (newOld.HasValue && newOld.Value <= newPrice) newOld = null;
            return (newPrice, newOld);
        }

        // 3) Fall back to parent pricing untouched
        return (parentPrice, parentOld);
    }

    /// <summary>Builds the child SKU from the parent SKU + sanitized variant
    /// value. Keeps the result short and ASCII-safe so it survives the
    /// VARCHAR(64) Bagisto column.</summary>
    private static string BuildChildSku(string parentSku, string variantValue, int position)
    {
        var slug = Sanitize(variantValue);
        if (string.IsNullOrEmpty(slug)) slug = $"v{position}";
        var combined = $"{parentSku}-{slug}";
        return combined.Length > 60 ? combined[..60] : combined;
    }

    private static string BuildChildUrlKey(string parentUrlKey, string variantValue, int position)
    {
        var slug = Sanitize(variantValue);
        if (string.IsNullOrEmpty(slug)) slug = $"v{position}";
        var combined = $"{parentUrlKey}-{slug}";
        return Truncate(combined, MaxUrlKeyLen) ?? combined;
    }

    // ─── Entry point ─────────────────────────────────────────────────────

    /// <summary>Seeds from the legacy <c>Data/scraped_data.json</c> file
    /// produced by the old BagistoScraper project.</summary>
    public static async Task SeedAsync(BagistoDbContext db, bool forceReseed = false)
    {
        var jsonPath = LocateJsonFile();
        if (jsonPath == null)
        {
            Console.WriteLine($"[Seeder] {ScrapedDataFileName} not found. Run BagistoScraper first.");
            return;
        }

        Console.WriteLine($"[Seeder] Loading {jsonPath}...");
        await using var stream = File.OpenRead(jsonPath);
        var sites = await JsonSerializer.DeserializeAsync<List<ScrapedRoot>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new();

        await SeedCoreAsync(db, sites, forceReseed, jsonPath);
    }

    /// <summary>Seeds from the <c>dd_scraped_products</c> /
    /// <c>dd_scraped_categories</c> staging tables populated by the Python
    /// DigitalDarsiScraper project. This is the current data source — it
    /// carries the full scrape (distinct EN/TE text, specification tables,
    /// richer descriptions) that the old JSON file did not.</summary>
    public static async Task SeedFromStagingAsync(BagistoDbContext db, bool forceReseed = false)
    {
        var sites = await LoadFromStagingAsync(db);
        if (sites.Count == 0)
        {
            Console.WriteLine("[Seeder] Staging tables empty or missing. "
                + "Run the DigitalDarsiScraper import first.");
            return;
        }
        await SeedCoreAsync(db, sites, forceReseed, "dd_scraped_* staging tables");
    }

    /// <summary>Shared orchestration: the already-seeded guard, the wipe of
    /// any previous Digital Darsi generation, and the per-site seed.
    /// Both data sources funnel through here so the catalogue is built
    /// identically regardless of where the scrape came from.</summary>
    private static async Task SeedCoreAsync(
        BagistoDbContext db, List<ScrapedRoot> sites, bool forceReseed, string sourceLabel)
    {
        var alreadySeeded = await db.Products
            .AnyAsync(p => p.Additional != null && p.Additional.Contains(SentinelPrefix));
        if (alreadySeeded && !forceReseed)
        {
            Console.WriteLine("[Seeder] Digital Darsi data already seeded. Skipping (pass forceReseed=true to wipe).");
            return;
        }

        if (alreadySeeded && forceReseed)
        {
            Console.WriteLine("[Seeder] forceReseed=true -> wiping existing Digital Darsi data...");
            await WipeExistingAsync(db);
        }

        Console.WriteLine($"[Seeder] Loaded {sites.Count} sites from {sourceLabel} with "
            + $"{sites.Sum(s => s.Categories.Count)} categories, "
            + $"{sites.Sum(s => s.Products.Count)} products.");

        var rootCategory = await EnsureRootCategoryAsync(db);
        var attrFamily = await EnsureAttributeFamilyAsync(db);

        var lftCounter = Math.Max(rootCategory.Rgt, 100);

        foreach (var site in sites)
        {
            Console.WriteLine($"\n[Seeder] === {site.Key} ({site.Products.Count} products) ===");
            await SeedSiteAsync(db, site, rootCategory, attrFamily.Id, () => lftCounter++);
        }

        // Repair root category bounds
        var maxLft = await db.Categories.MaxAsync(c => (int?)c.Lft) ?? 1;
        var maxRgt = await db.Categories.MaxAsync(c => (int?)c.Rgt) ?? 1;
        rootCategory.Rgt = Math.Max(maxRgt, maxLft) + 1;
        await db.SaveChangesAsync();

        Console.WriteLine($"\n[Seeder] Done!");
    }

    // ─── Staging-table loader ────────────────────────────────────────────

    /// <summary>Reads the <c>dd_scraped_*</c> staging tables (written by the
    /// Python scraper's import step) into the same <see cref="ScrapedRoot"/>
    /// shape the JSON path produces, so all the downstream transformation
    /// logic is reused unchanged.</summary>
    private static async Task<List<ScrapedRoot>> LoadFromStagingAsync(BagistoDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        // Site key -> root accumulator.
        var bySite = new Dictionary<string, ScrapedRoot>(StringComparer.OrdinalIgnoreCase);

        ScrapedRoot RootFor(string siteKey)
        {
            if (!bySite.TryGetValue(siteKey, out var root))
            {
                root = new ScrapedRoot
                {
                    Key = siteKey,
                    DisplayName = SiteNames.TryGetValue(siteKey, out var n) ? n.En : siteKey,
                    BaseUrl = "",
                };
                bySite[siteKey] = root;
            }
            return root;
        }

        // --- categories ---
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT site_key, slug, url, name_en, name_te, " +
                "description_html_en, description_html_te, description_en, description_te, " +
                "image, parent_slug FROM dd_scraped_categories";
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                string S(int i) => rd.IsDBNull(i) ? "" : rd.GetString(i);
                var siteKey = S(0);
                if (string.IsNullOrWhiteSpace(siteKey)) continue;
                var descEn = S(5);
                if (string.IsNullOrWhiteSpace(descEn)) descEn = S(7);
                var descTe = S(6);
                if (string.IsNullOrWhiteSpace(descTe)) descTe = S(8);
                RootFor(siteKey).Categories.Add(new ScrapedCategoryDto
                {
                    SiteKey = siteKey,
                    Slug = S(1),
                    Url = S(2),
                    NameEn = S(3),
                    NameTe = S(4),
                    DescriptionEn = string.IsNullOrWhiteSpace(descEn) ? null : descEn,
                    DescriptionTe = string.IsNullOrWhiteSpace(descTe) ? null : descTe,
                    Image = string.IsNullOrWhiteSpace(S(9)) ? null : S(9),
                    ParentSlug = string.IsNullOrWhiteSpace(S(10)) ? null : S(10),
                });
            }
        }

        // --- products (SELECT * so the dynamic spec_* / detail_* columns
        //     come along; we read everything by column name) ---
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM dd_scraped_products";
            await using var rd = await cmd.ExecuteReaderAsync();

            // Map column name -> ordinal once.
            var ord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rd.FieldCount; i++) ord[rd.GetName(i)] = i;

            string Get(string col)
            {
                if (!ord.TryGetValue(col, out var i) || rd.IsDBNull(i)) return "";
                var v = rd.GetValue(i);
                return v?.ToString() ?? "";
            }

            while (await rd.ReadAsync())
            {
                var siteKey = Get("site_key");
                if (string.IsNullOrWhiteSpace(siteKey)) continue;

                int.TryParse(Get("source_product_id"), out var sourceId);
                var slug = Get("url");
                var sku = Get("sku");
                if (string.IsNullOrWhiteSpace(sku))
                {
                    // The scrape rarely exposes a real SKU; synthesise a
                    // stable one the same way the old scraper did.
                    sku = sourceId > 0
                        ? $"{siteKey.ToUpperInvariant()}{sourceId:D6}"
                        : $"{siteKey.ToUpperInvariant()}-{Math.Abs(slug.GetHashCode()):D8}";
                }

                decimal.TryParse(Get("price_value"), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var price);
                decimal? oldPrice = decimal.TryParse(Get("old_price_value"),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var op) && op > 0
                    ? op : null;

                var fullEn = Get("full_description_html_en");
                if (string.IsNullOrWhiteSpace(fullEn)) fullEn = Get("full_description_en");
                var fullTe = Get("full_description_html_te");
                if (string.IsNullOrWhiteSpace(fullTe)) fullTe = Get("full_description_te");

                var dto = new ScrapedProductDto
                {
                    SiteKey = siteKey,
                    Url = slug,
                    SourceProductId = sourceId,
                    Sku = sku,
                    NameEn = Get("name_en"),
                    NameTe = Get("name_te"),
                    ShortDescriptionEn = NullIfBlank(Get("short_description_en")),
                    ShortDescriptionTe = NullIfBlank(Get("short_description_te")),
                    FullDescriptionEn = NullIfBlank(fullEn),
                    FullDescriptionTe = NullIfBlank(fullTe),
                    Vendor = NullIfBlank(Get("vendor")),
                    Price = price,
                    OldPrice = oldPrice,
                    Images = SplitImages(Get("images")),
                    Breadcrumbs = ParseBreadcrumbs(Get("breadcrumbs_json")),
                    Variations = ParseVariations(Get("variations_json")),
                    Specs = CollectSpecs(ord, Get),
                };
                RootFor(siteKey).Products.Add(dto);
            }
        }

        return bySite.Values.ToList();
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    private static List<string> SplitImages(string joined) =>
        string.IsNullOrWhiteSpace(joined)
            ? new List<string>()
            : joined.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

    /// <summary>Every <c>spec_*</c> column with a value becomes a spec entry.
    /// Column names like <c>spec_maximum_shelf_life</c> are turned back into a
    /// readable label ("Maximum Shelf Life"). The <c>detail_*</c> staging
    /// columns are deliberately skipped — they hold noisy, inconsistently
    /// labelled "additional details" scrapings, not the real specification
    /// table.</summary>
    private static Dictionary<string, string>? CollectSpecs(
        Dictionary<string, int> ord,
        Func<string, string> get)
    {
        Dictionary<string, string>? specs = null;
        foreach (var (name, _) in ord)
        {
            if (!name.StartsWith("spec_", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = get(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            // Drop the "spec_" prefix, then title-case the remaining words.
            var bare = name[(name.IndexOf('_') + 1)..];
            var label = string.Join(' ', bare.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
            if (string.IsNullOrWhiteSpace(label)) continue;
            (specs ??= new())[label] = value;
        }
        return specs;
    }

    private static List<ScrapedBreadcrumbDto> ParseBreadcrumbs(string json)
    {
        var result = new List<ScrapedBreadcrumbDto>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                result.Add(new ScrapedBreadcrumbDto
                {
                    NameEn = el.TryGetProperty("name_en", out var ne) ? ne.GetString() ?? "" : "",
                    NameTe = el.TryGetProperty("name_te", out var nt) ? nt.GetString() ?? "" : "",
                    Href = el.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "",
                });
            }
        }
        catch (JsonException) { /* skip malformed */ }
        return result;
    }

    private static List<ScrapedVariationDto> ParseVariations(string json)
    {
        var result = new List<ScrapedVariationDto>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                result.Add(new ScrapedVariationDto
                {
                    Label = el.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                    Value = el.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                });
            }
        }
        catch (JsonException) { /* skip malformed */ }
        return result;
    }

    // ─── Per-site seeding ────────────────────────────────────────────────

    private static async Task SeedSiteAsync(
        BagistoDbContext db,
        ScrapedRoot site,
        Category rootCategory,
        int attrFamilyId,
        Func<int> nextLft)
    {
        var (siteNameEn, siteNameTe) = SiteNames.TryGetValue(site.Key, out var n)
            ? n : (site.DisplayName, site.DisplayName);
        var siteSlug = $"dd-{site.Key}";

        // 1. Top-level site category
        var siteCategory = await CreateCategoryAsync(
            db,
            parentId: rootCategory.Id,
            nameEn: siteNameEn,
            nameTe: siteNameTe,
            slug: siteSlug,
            descriptionEn: $"{siteNameEn} — from {site.BaseUrl}",
            descriptionTe: $"{siteNameTe} — from {site.BaseUrl}",
            image: site.Products.FirstOrDefault()?.Images.FirstOrDefault(),
            nextLft: nextLft);

        // 2. Build category map: source slug -> Category
        //    First pass: create categories from the scraped list.
        var categoryMap = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);

        // Breadcrumb hrefs and sitemap URLs use different slug styles on the
        // source sites — the sitemap emits Telugu-encoded paths while the
        // breadcrumb <a> hrefs use the English slug — so a category like
        // "Staples" appears under two different scraped slugs. To avoid
        // creating duplicate DB categories, we also dedupe by NAME against
        // site.Categories before adding a breadcrumb-derived entry.
        var siteCatNameLookupEn = new HashSet<string>(
            site.Categories.Where(c => !string.IsNullOrWhiteSpace(c.NameEn)).Select(c => c.NameEn),
            StringComparer.OrdinalIgnoreCase);
        var siteCatNameLookupTe = new HashSet<string>(
            site.Categories.Where(c => !string.IsNullOrWhiteSpace(c.NameTe)).Select(c => c.NameTe),
            StringComparer.OrdinalIgnoreCase);

        var categoriesFromBreadcrumbs = new List<ScrapedCategoryDto>();
        foreach (var p in site.Products)
        {
            for (int i = 0; i < p.Breadcrumbs.Count; i++)
            {
                var bc = p.Breadcrumbs[i];
                var slug = NormalizeSlug(bc.Href);
                if (string.IsNullOrEmpty(slug)) continue;
                if (site.Categories.Any(c => c.Slug == slug)) continue;
                if (categoriesFromBreadcrumbs.Any(c => c.Slug == slug)) continue;
                // Also skip if a site.Category already exists with this name
                // in either locale — that's the same logical category under a
                // different slug.
                if (!string.IsNullOrWhiteSpace(bc.NameEn) && siteCatNameLookupEn.Contains(bc.NameEn)) continue;
                if (!string.IsNullOrWhiteSpace(bc.NameTe) && siteCatNameLookupTe.Contains(bc.NameTe)) continue;

                var parentSlug = i > 0
                    ? NormalizeSlug(p.Breadcrumbs[i - 1].Href)
                    : null;

                categoriesFromBreadcrumbs.Add(new ScrapedCategoryDto
                {
                    SiteKey = site.Key,
                    Slug = slug,
                    NameEn = bc.NameEn,
                    NameTe = bc.NameTe,
                    ParentSlug = parentSlug,
                });
            }
        }
        var allCategories = site.Categories
            .Select(c => new ScrapedCategoryDto
            {
                SiteKey = c.SiteKey,
                Url = c.Url,
                Slug = c.Slug,
                NameEn = c.NameEn,
                NameTe = c.NameTe,
                DescriptionEn = c.DescriptionEn,
                DescriptionTe = c.DescriptionTe,
                Image = c.Image,
                ParentSlug = c.ParentSlug,
            })
            .Concat(categoriesFromBreadcrumbs)
            .GroupBy(c => c.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Sort categories so parents are created before children
        var ordered = TopoSortCategories(allCategories);

        foreach (var cat in ordered)
        {
            var parentId = siteCategory.Id;
            if (!string.IsNullOrEmpty(cat.ParentSlug) && categoryMap.TryGetValue(cat.ParentSlug, out var parent))
                parentId = parent.Id;

            var created = await CreateCategoryAsync(
                db,
                parentId: parentId,
                nameEn: !string.IsNullOrEmpty(cat.NameEn) ? cat.NameEn : cat.NameTe,
                nameTe: !string.IsNullOrEmpty(cat.NameTe) ? cat.NameTe : cat.NameEn,
                slug: $"{siteSlug}-{Sanitize(cat.Slug)}",
                descriptionEn: cat.DescriptionEn ?? cat.DescriptionTe,
                descriptionTe: cat.DescriptionTe ?? cat.DescriptionEn,
                image: cat.Image,
                nextLft: nextLft);
            categoryMap[cat.Slug] = created;
        }

        Console.WriteLine($"[Seeder]   {categoryMap.Count + 1} categories created");

        // Build a name → Category lookup. Products reference categories via
        // breadcrumb hrefs which (for the scraped sites) are English slugs —
        // but the displayed categories mostly come from site.Categories with
        // Telugu slugs. Matching by name closes that gap so products land on
        // the category the app actually renders.
        var categoryByName = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in allCategories)
        {
            if (!categoryMap.TryGetValue(c.Slug, out var dbCat)) continue;
            if (!string.IsNullOrWhiteSpace(c.NameEn)) categoryByName[c.NameEn] = dbCat;
            if (!string.IsNullOrWhiteSpace(c.NameTe)) categoryByName[c.NameTe] = dbCat;
        }

        // 3. Products
        int productCount = 0;
        foreach (var p in site.Products)
        {
            // Determine which category this product belongs to. Walk the
            // breadcrumbs from most specific to least specific and prefer
            // name-based matching (locale-agnostic) over slug-based matching.
            Category target = siteCategory;
            for (int i = p.Breadcrumbs.Count - 1; i >= 0; i--)
            {
                var bc = p.Breadcrumbs[i];
                if (!string.IsNullOrWhiteSpace(bc.NameEn) && categoryByName.TryGetValue(bc.NameEn, out var byNameEn))
                {
                    target = byNameEn;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(bc.NameTe) && categoryByName.TryGetValue(bc.NameTe, out var byNameTe))
                {
                    target = byNameTe;
                    break;
                }
                var slug = NormalizeSlug(bc.Href);
                if (slug != null && categoryMap.TryGetValue(slug, out var found))
                {
                    target = found;
                    break;
                }
            }

            await CreateProductAsync(db, site, p, target, attrFamilyId);
            productCount++;
            if (productCount % 50 == 0)
                Console.WriteLine($"[Seeder]   ... {productCount}/{site.Products.Count} products");
        }
        Console.WriteLine($"[Seeder]   {productCount} products created");
    }

    // ─── Category helpers ────────────────────────────────────────────────

    private static async Task<Category> EnsureRootCategoryAsync(BagistoDbContext db)
    {
        var root = await db.Categories.FirstOrDefaultAsync(c => c.ParentId == null);
        if (root != null) return root;
        root = new Category
        {
            Position = 1,
            Status = true,
            DisplayMode = "products_and_description",
            Lft = 1,
            Rgt = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Categories.Add(root);
        await db.SaveChangesAsync();
        db.CategoryTranslations.Add(new CategoryTranslation
        {
            CategoryId = root.Id,
            Name = "Root",
            Slug = "root",
            UrlPath = "",
            Locale = "en",
        });
        await db.SaveChangesAsync();
        return root;
    }

    private static async Task<AttributeFamily> EnsureAttributeFamilyAsync(BagistoDbContext db)
    {
        var fam = await db.AttributeFamilies.FirstOrDefaultAsync();
        if (fam != null) return fam;
        fam = new AttributeFamily
        {
            Name = "Default",
            Code = "default",
            Status = true,
            IsUserDefined = false,
        };
        db.AttributeFamilies.Add(fam);
        await db.SaveChangesAsync();
        return fam;
    }

    private static async Task<Category> CreateCategoryAsync(
        BagistoDbContext db,
        int parentId,
        string nameEn,
        string nameTe,
        string slug,
        string? descriptionEn,
        string? descriptionTe,
        string? image,
        Func<int> nextLft)
    {
        slug = Truncate(slug, MaxUrlKeyLen) ?? slug;
        // Reuse existing category with this slug if present (idempotent).
        // If a prior failed/partial run left rows with empty names, refresh
        // them here so a re-seed always produces a clean, named category.
        var existingSlug = await db.CategoryTranslations
            .FirstOrDefaultAsync(ct => ct.Slug == slug && ct.Locale == "en");
        if (existingSlug != null)
        {
            var existingCat = await db.Categories.FindAsync(existingSlug.CategoryId);
            if (existingCat != null)
            {
                var safeNameEnExisting = Truncate(nameEn, MaxNameLen) ?? "";
                var safeNameTeExisting = Truncate(nameTe, MaxNameLen) ?? safeNameEnExisting;
                var translations = await db.CategoryTranslations
                    .Where(ct => ct.CategoryId == existingCat.Id)
                    .ToListAsync();
                foreach (var t in translations)
                {
                    if (t.Locale == "en" && string.IsNullOrEmpty(t.Name)) t.Name = safeNameEnExisting;
                    if (t.Locale == "te" && string.IsNullOrEmpty(t.Name)) t.Name = safeNameTeExisting;
                }
                await db.SaveChangesAsync();
                return existingCat;
            }
        }

        var category = new Category
        {
            ParentId = parentId,
            Position = 1,
            Status = true,
            DisplayMode = "products_and_description",
            LogoPath = image,
            BannerPath = image,
            Lft = nextLft(),
            Rgt = nextLft(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var safeNameEn = Truncate(nameEn, MaxNameLen) ?? "";
        var safeNameTe = Truncate(nameTe, MaxNameLen) ?? safeNameEn;
        var safeSlug = Truncate(slug, MaxUrlKeyLen) ?? slug;

        db.CategoryTranslations.Add(new CategoryTranslation
        {
            CategoryId = category.Id,
            Name = safeNameEn,
            Slug = safeSlug,
            UrlPath = safeSlug,
            Description = descriptionEn,
            Locale = "en",
        });
        // Always write the te row — even if text matches en — so an Accept-Language:
        // te request never has to fall back to the en row. The locale switch must
        // return a row that actually exists for its locale.
        db.CategoryTranslations.Add(new CategoryTranslation
        {
            CategoryId = category.Id,
            Name = safeNameTe,
            Slug = safeSlug,
            UrlPath = safeSlug,
            Description = descriptionTe,
            Locale = "te",
        });
        await db.SaveChangesAsync();
        return category;
    }

    // ─── Product helper ──────────────────────────────────────────────────

    // Column caps match the Bagisto MySQL schema. VARCHAR(255) on name/slug
    // fields; we truncate aggressively (names > 250 chars are marketing
    // salad that'll be clipped in the UI anyway). Descriptions go into
    // TEXT columns so we leave them uncapped.
    private const int MaxNameLen = 250;
    private const int MaxShortDescLen = 1000;
    private const int MaxVendorLen = 200;
    private const int MaxUrlKeyLen = 200;

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..max];
    }

    private static async Task CreateProductAsync(
        BagistoDbContext db,
        ScrapedRoot site,
        ScrapedProductDto p,
        Category category,
        int attrFamilyId)
    {
        // Fall back between locales if one side is blank — better to duplicate
        // text than leave a row empty.
        var nameEnRaw = !string.IsNullOrWhiteSpace(p.NameEn) ? p.NameEn : p.NameTe;
        var nameTeRaw = !string.IsNullOrWhiteSpace(p.NameTe) ? p.NameTe : p.NameEn;
        if (string.IsNullOrWhiteSpace(nameEnRaw) || p.Price <= 0) return;

        var existing = await db.Products.FirstOrDefaultAsync(x => x.Sku == p.Sku);
        if (existing != null) return; // idempotent

        var additional = new ProductAdditional
        {
            Site = site.Key,
            Vendor = Truncate(p.Vendor, MaxVendorLen),
            SourceUrl = p.Url,
            SourceId = p.SourceProductId > 0 ? p.SourceProductId : null,
            Variations = p.Variations?.Count > 0
                ? p.Variations.Select(v => new KeyValuePair<string, string>(v.Label, v.Value)).ToList()
                : null,
            Specs = p.Specs is { Count: > 0 } ? p.Specs : null,
        };

        var nameEn = Truncate(nameEnRaw, MaxNameLen)!;
        var nameTe = Truncate(nameTeRaw, MaxNameLen)!;
        var shortDescEn = Truncate(p.ShortDescriptionEn ?? p.ShortDescriptionTe ?? nameEn, MaxShortDescLen);
        var shortDescTe = Truncate(p.ShortDescriptionTe ?? p.ShortDescriptionEn ?? nameTe, MaxShortDescLen);
        var fullDescEn = p.FullDescriptionEn ?? p.FullDescriptionTe ?? p.ShortDescriptionEn ?? nameEn;
        var fullDescTe = p.FullDescriptionTe ?? p.FullDescriptionEn ?? p.ShortDescriptionTe ?? nameTe;
        var urlKey = Truncate(p.Sku.ToLowerInvariant(), MaxUrlKeyLen)!;
        var regular = p.OldPrice ?? p.Price;
        var special = p.OldPrice.HasValue && p.OldPrice.Value > p.Price ? p.Price : (decimal?)null;

        // Decide whether this product should be configurable. We do this BEFORE
        // inserting the parent so the type column is right on the first write
        // (avoids a follow-up UPDATE for the 400ish products with variants).
        var cleanedVariations = CleanVariations(p.Variations);
        var hasVariants = cleanedVariations.Count > 0;
        var productType = hasVariants ? "configurable" : "simple";

        var product = new Product
        {
            Sku = p.Sku,
            Type = productType,
            AttributeFamilyId = attrFamilyId,
            Additional = JsonSerializer.Serialize(additional),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        product.Categories.Add(category);
        db.Products.Add(product);
        // Flush so product.Id is populated for the dependent rows below
        await db.SaveChangesAsync();

        // ProductFlat: one row per locale. Distinct en and te text from the
        // scrape. We hold references because Bagisto's product_flat schema
        // has its own parent_id column that FK's BACK to product_flat.id (not
        // products.id) — so child variant flats need the *parent flat* row id
        // matched by locale, not the parent product id.
        var parentFlatEn = new ProductFlat
        {
            ProductId = product.Id,
            Sku = p.Sku,
            Type = productType,
            Name = nameEn,
            ShortDescription = shortDescEn,
            Description = fullDescEn,
            UrlKey = urlKey,
            Status = true,
            New = true,
            Featured = false,
            VisibleIndividually = true,
            Price = regular,
            SpecialPrice = special,
            Weight = 1,
            Locale = "en",
            Channel = "default",
            AttributeFamilyId = attrFamilyId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var parentFlatTe = new ProductFlat
        {
            ProductId = product.Id,
            Sku = p.Sku,
            Type = productType,
            Name = nameTe,
            ShortDescription = shortDescTe,
            Description = fullDescTe,
            UrlKey = urlKey,
            Status = true,
            New = true,
            Featured = false,
            VisibleIndividually = true,
            Price = regular,
            SpecialPrice = special,
            Weight = 1,
            Locale = "te",
            Channel = "default",
            AttributeFamilyId = attrFamilyId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ProductFlats.Add(parentFlatEn);
        db.ProductFlats.Add(parentFlatTe);

        int pos = 1;
        foreach (var img in p.Images.Distinct())
        {
            if (string.IsNullOrWhiteSpace(img)) continue;
            db.ProductImages.Add(new ProductImage
            {
                ProductId = product.Id,
                Type = "image",
                Path = img,
                Position = pos++,
            });
        }

        // Single save for flats + images — ~3x fewer round-trips per product.
        // After this returns, parentFlatEn/Te.Id are populated.
        await db.SaveChangesAsync();

        // For configurable products, materialize each option as its own child
        // product with parent_id = product.Id. The child carries the
        // per-variant price + flat rows so the cart binds cleanly to whichever
        // weight/size the customer picked.
        if (hasVariants)
        {
            foreach (var cv in cleanedVariations)
            {
                await CreateChildVariantAsync(
                    db,
                    site,
                    p,
                    product,
                    attrFamilyId,
                    cv,
                    parentUrlKey: urlKey,
                    parentNameEn: nameEn,
                    parentNameTe: nameTe,
                    parentFlatEnId: parentFlatEn.Id,
                    parentFlatTeId: parentFlatTe.Id);
            }
        }
    }

    /// <summary>Creates one child variant under <paramref name="parent"/>.
    /// The child is purely transactional: <c>type = "simple"</c>, no images
    /// of its own (it inherits the parent's gallery via the API), no category
    /// mapping (so it doesn't leak into category browse listings — the parent
    /// is the catalog face), and locale-aware <see cref="ProductFlat"/> rows
    /// so cart lines render in whichever language the user is browsing.</summary>
    private static async Task CreateChildVariantAsync(
        BagistoDbContext db,
        ScrapedRoot site,
        ScrapedProductDto scraped,
        Product parent,
        int attrFamilyId,
        CleanedVariation cv,
        string parentUrlKey,
        string parentNameEn,
        string parentNameTe,
        int parentFlatEnId,
        int parentFlatTeId)
    {
        var (childPrice, childOldPrice) = ComputeVariantPrice(scraped, cv);
        // Below ₹1 is almost certainly a prorate underflow on a tiny weight
        // (e.g. 50g of a parent with no real per-gram price). Skip — listing
        // these as ₹0 / ₹1 mocks the customer.
        if (childPrice < 1m) return;

        var childRegular = childOldPrice ?? childPrice;
        var childSpecial = childOldPrice.HasValue && childOldPrice.Value > childPrice
            ? childPrice
            : (decimal?)null;

        var childSku = BuildChildSku(parent.Sku, cv.Value, cv.Position);
        // SKU collision guard — Sanitize() can squash distinct values down to
        // the same slug if they only differ in punctuation. Append the position
        // as a deterministic disambiguator when that happens.
        if (await db.Products.AnyAsync(x => x.Sku == childSku))
        {
            childSku = Truncate($"{childSku}-{cv.Position}", 60) ?? childSku;
            if (await db.Products.AnyAsync(x => x.Sku == childSku)) return;
        }

        var childUrlKey = BuildChildUrlKey(parentUrlKey, cv.Value, cv.Position);

        // Variant chip text comes from cv.Value ("50g") — the API surfaces it
        // back to Flutter via the ChildVariantAdditional JSON below, which
        // means the chip stays clean even if the cart-facing name is
        // "Bangala dumpa - 50g".
        var childNameEn = Truncate($"{parentNameEn} - {cv.Value}", MaxNameLen) ?? parentNameEn;
        var childNameTe = Truncate($"{parentNameTe} - {cv.Value}", MaxNameLen) ?? parentNameTe;

        var childAdditional = new ChildVariantAdditional
        {
            Site = site.Key,
            ParentSku = parent.Sku,
            VariantValue = cv.Value,
            VariantLabel = cv.Label,
            VariantPosition = cv.Position,
        };

        var child = new Product
        {
            Sku = childSku,
            ParentId = parent.Id,
            Type = "simple",
            AttributeFamilyId = attrFamilyId,
            Additional = JsonSerializer.Serialize(childAdditional),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Products.Add(child);
        await db.SaveChangesAsync();

        db.ProductFlats.Add(new ProductFlat
        {
            ProductId = child.Id,
            // FK references the parent's product_flat row in the same locale,
            // not the parent product row.
            ParentId = parentFlatEnId,
            Sku = childSku,
            Type = "simple",
            Name = childNameEn,
            UrlKey = childUrlKey,
            Status = true,
            New = false,
            Featured = false,
            // Children are not browsable on their own — only via the
            // configurable parent. This matches Bagisto's convention and
            // keeps category/search listings clean.
            VisibleIndividually = false,
            Price = childRegular,
            SpecialPrice = childSpecial,
            Weight = 1,
            Locale = "en",
            Channel = "default",
            AttributeFamilyId = attrFamilyId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.ProductFlats.Add(new ProductFlat
        {
            ProductId = child.Id,
            ParentId = parentFlatTeId,
            Sku = childSku,
            Type = "simple",
            Name = childNameTe,
            UrlKey = childUrlKey,
            Status = true,
            New = false,
            Featured = false,
            VisibleIndividually = false,
            Price = childRegular,
            SpecialPrice = childSpecial,
            Weight = 1,
            Locale = "te",
            Channel = "default",
            AttributeFamilyId = attrFamilyId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ─── Wipe (for force re-seed) ────────────────────────────────────────

    // Slugs created by the previous hand-coded DigitalDarsiSeeder. We delete
    // categories matching these along with the v2 data so forceReseed leaves
    // a clean slate.
    private static readonly string[] LegacyTopLevelSlugs =
    {
        "build-store", "food-store", "digital-store", "services",
    };

    private static async Task WipeExistingAsync(BagistoDbContext db)
    {
        // 1. Products from any previous Digital Darsi seeder generation
        //    (identified by the shared sentinel prefix in Additional).
        var v2ProductIds = await db.Products
            .Where(p => p.Additional != null && p.Additional.Contains(SentinelPrefix))
            .Select(p => p.Id)
            .ToListAsync();

        // 2. Legacy category subtrees — collect root ids, then find all
        //    descendant categories via the parent chain.
        var legacyRootIds = await db.CategoryTranslations
            .Where(ct => LegacyTopLevelSlugs.Contains(ct.Slug))
            .Select(ct => ct.CategoryId)
            .Distinct()
            .ToListAsync();

        var legacyCatIds = new HashSet<int>(legacyRootIds);
        bool added;
        do
        {
            added = false;
            var children = await db.Categories
                .Where(c => c.ParentId != null && legacyCatIds.Contains(c.ParentId.Value) && !legacyCatIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync();
            foreach (var id in children) added |= legacyCatIds.Add(id);
        } while (added);

        // Also include v2 category roots
        var v2CatIds = await db.CategoryTranslations
            .Where(ct => ct.Slug.StartsWith("dd-"))
            .Select(ct => ct.CategoryId)
            .Distinct()
            .ToListAsync();
        foreach (var id in v2CatIds) legacyCatIds.Add(id);

        // 3. Products linked to any of those legacy/v2 categories
        var linkedProductIds = await db.Products
            .Where(p => p.Categories.Any(c => legacyCatIds.Contains(c.Id)))
            .Select(p => p.Id)
            .ToListAsync();

        var allProductIds = v2ProductIds.Concat(linkedProductIds).Distinct().ToList();

        if (allProductIds.Count > 0)
        {
            Console.WriteLine($"[Seeder]   deleting {allProductIds.Count} products (v2 + legacy)");
            await db.ProductImages.Where(pi => allProductIds.Contains(pi.ProductId)).ExecuteDeleteAsync();
            await db.ProductFlats.Where(pf => allProductIds.Contains(pf.ProductId)).ExecuteDeleteAsync();
            await db.ProductInventories.Where(pi => allProductIds.Contains(pi.ProductId)).ExecuteDeleteAsync();
            await db.ProductPriceIndices.Where(ppi => allProductIds.Contains(ppi.ProductId)).ExecuteDeleteAsync();
            // M2M link table: SQL in batches of 500 to avoid oversized IN lists
            for (int i = 0; i < allProductIds.Count; i += 500)
            {
                var chunk = allProductIds.Skip(i).Take(500);
                var idList = string.Join(",", chunk);
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM product_categories WHERE product_id IN ({idList})");
            }
            await db.Products.Where(p => allProductIds.Contains(p.Id)).ExecuteDeleteAsync();
        }

        if (legacyCatIds.Count > 0)
        {
            Console.WriteLine($"[Seeder]   deleting {legacyCatIds.Count} categories (v2 + legacy)");
            // Delete children before parents (ExecuteDeleteAsync ignores nav cascades)
            // Order by descending Lft so deepest come first.
            var ordered = await db.Categories
                .Where(c => legacyCatIds.Contains(c.Id))
                .OrderByDescending(c => c.Lft)
                .Select(c => c.Id)
                .ToListAsync();
            await db.CategoryTranslations.Where(ct => ordered.Contains(ct.CategoryId)).ExecuteDeleteAsync();
            await db.Categories.Where(c => ordered.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }

    // ─── Utility helpers ─────────────────────────────────────────────────

    private static string? LocateJsonFile()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Data", ScrapedDataFileName),
            System.IO.Path.Combine(AppContext.BaseDirectory, ScrapedDataFileName),
            System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Data", ScrapedDataFileName),
            System.IO.Path.Combine(Directory.GetCurrentDirectory(), ScrapedDataFileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? NormalizeSlug(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        href = href.TrimStart('/');
        var q = href.IndexOf('?');
        if (q >= 0) href = href[..q];
        return System.Net.WebUtility.UrlDecode(href);
    }

    private static string Sanitize(string slug)
    {
        // Bagisto URL keys should be ASCII-safe; replace non-alphanum with hyphen
        var sb = new System.Text.StringBuilder();
        foreach (var c in slug)
        {
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var r = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(r) ? Math.Abs(slug.GetHashCode()).ToString("x") : r;
    }

    private static List<ScrapedCategoryDto> TopoSortCategories(List<ScrapedCategoryDto> cats)
    {
        var bySlug = cats.ToDictionary(c => c.Slug, c => c, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScrapedCategoryDto>();

        void Visit(ScrapedCategoryDto c)
        {
            if (!visited.Add(c.Slug)) return;
            if (!string.IsNullOrEmpty(c.ParentSlug) && bySlug.TryGetValue(c.ParentSlug, out var parent))
                Visit(parent);
            result.Add(c);
        }
        foreach (var c in cats) Visit(c);
        return result;
    }
}
