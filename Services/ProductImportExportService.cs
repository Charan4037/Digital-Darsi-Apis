using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Catalog;

namespace DOSApi.Services;

/// <summary>
/// Bulk product Excel import/export for the admin "Import/Export Products"
/// screen. Export reuses AdminGlobalProductsController.List's exact
/// search/filter/category/vendor logic (minus paging) so "export what I'm
/// looking at" always matches what the filtered list screen shows. Import
/// re-parses the same column layout in batches of <see cref="BatchSize"/>
/// rows — see ImportAsync for why batching (not one big transaction) is the
/// whole point: a bad row in one batch never blocks the other batches, and
/// EF Core's automatic new-entity key propagation lets a whole batch of new
/// products insert in a single round trip, which matters a lot against this
/// app's high-latency remote MySQL host.
/// </summary>
public class ProductImportExportService
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;
    private readonly VendorAggregationService _aggregation;
    private readonly ILogger<ProductImportExportService> _logger;

    /// <summary>
    /// Rows per import chunk — per the team's explicit ask: a 100-row file
    /// imports as 50 + 50, so if one chunk fails outright the other chunk
    /// still lands instead of the whole file failing together.
    /// </summary>
    public const int BatchSize = 50;

    // Single source of truth for column order — used by the template, the
    // export, AND read back by the importer, so a freshly exported file (or
    // the downloaded template) always re-imports without reformatting.
    private static readonly string[] Headers =
    {
        "Product ID", "SKU", "Parent SKU", "Row Ref", "Variant Value",
        "Name (English)", "Name (Telugu)",
        "Category ID", "Category Name", "Category Name (Telugu)",
        "Vendor ID", "Vendor Name", "Vendor Name (Telugu)",
        "Price", "Special Price", "Stock Qty", "Min Qty", "Max Qty", "Active (Yes/No)",
        "Short Description (English)", "Short Description (Telugu)",
        "Description (English)", "Description (Telugu)"
    };

    public ProductImportExportService(
        DOSDbContext db,
        ProductService productService,
        VendorAggregationService aggregation,
        ILogger<ProductImportExportService> logger)
    {
        _db = db;
        _productService = productService;
        _aggregation = aggregation;
        _logger = logger;
    }

    // ─── Export ──────────────────────────────────────────────────────────

    /// <summary>Exports every product matching the given filters — the same filter semantics as AdminGlobalProductsController.List, minus paging.</summary>
    public async Task<byte[]> ExportAsync(string? search, string? filter, int? categoryId, string? vendorName)
    {
        var query = _db.Products.Where(p => p.ParentId == null).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(p =>
                p.Flats.Any(f => f.Name != null && f.Name.ToLower().Contains(searchLower)) ||
                (p.Sku != null && p.Sku.ToLower().Contains(searchLower)));
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            switch (filter.ToLower())
            {
                case "instock":
                    query = query.Where(p => p.Inventories.Any(i => i.Qty > 0));
                    break;
                case "outofstock":
                    query = query.Where(p => !p.Inventories.Any(i => i.Qty > 0));
                    break;
                case "active":
                    query = query.Where(p => p.Flats.Any(f => f.Status == true));
                    break;
                case "inactive":
                    query = query.Where(p => p.Flats.Any(f => f.Status != true));
                    break;
            }
        }

        if (categoryId.HasValue)
        {
            var subtreeIds = await _productService.GetSubtreeCategoryIdsAsync(categoryId.Value);
            query = query.Where(p => p.Categories.Any(c => subtreeIds.Contains(c.Id)));
        }

        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
            var vendorProductIds = productVendorMap
                .Where(kv => string.Equals(kv.Value, vendorName, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToHashSet();
            query = query.Where(p => vendorProductIds.Contains(p.Id));
        }

        var rows = await query
            .OrderBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Additional,
                NameEn = p.Flats.FirstOrDefault(f => f.Locale == "en")!.Name,
                NameTe = p.Flats.FirstOrDefault(f => f.Locale == "te")!.Name,
                Price = p.Flats.FirstOrDefault(f => f.Locale == "en")!.Price,
                SpecialPrice = p.Flats.FirstOrDefault(f => f.Locale == "en")!.SpecialPrice,
                MinQty = p.Flats.FirstOrDefault(f => f.Locale == "en")!.MinQty,
                MaxQty = p.Flats.FirstOrDefault(f => f.Locale == "en")!.MaxQty,
                Active = p.Flats.Any(f => f.Status == true),
                StockQty = p.Inventories.Sum(i => (int?)i.Qty) ?? 0,
                CategoryId = p.Categories.FirstOrDefault() != null ? (int?)p.Categories.FirstOrDefault()!.Id : null,
                CategoryName = p.Categories.FirstOrDefault() != null
                    ? p.Categories.FirstOrDefault()!.Translations.FirstOrDefault(t => t.Locale == "en")!.Name
                    : null,
                CategoryNameTe = p.Categories.FirstOrDefault() != null
                    ? p.Categories.FirstOrDefault()!.Translations.FirstOrDefault(t => t.Locale == "te")!.Name
                    : null,
                ShortDescriptionEn = p.Flats.FirstOrDefault(f => f.Locale == "en")!.ShortDescription,
                ShortDescriptionTe = p.Flats.FirstOrDefault(f => f.Locale == "te")!.ShortDescription,
                DescriptionEn = p.Flats.FirstOrDefault(f => f.Locale == "en")!.Description,
                DescriptionTe = p.Flats.FirstOrDefault(f => f.Locale == "te")!.Description,
            })
            .ToListAsync();

        // Products carry no vendor FK (only the free-text name in Additional),
        // so the export's Vendor ID/Vendor Name (Telugu) columns are resolved
        // by matching that name against the vendors table — same lookup
        // ProductService.GetProductVendor and VendorAggregationService use
        // elsewhere. A product whose stored name doesn't exactly match any
        // vendor row exports with a blank ID, falling back to whatever
        // Telugu text (if any) is embedded in its own Additional JSON.
        var vendorByName = await _db.Vendors.ToDictionaryAsync(v => v.Name, v => v, StringComparer.OrdinalIgnoreCase);

        // Variants (ParentId != null) are real, independently-priced Product
        // rows — see the "Variants" section of AdminGlobalProductsController.
        // They're pulled separately (children have no category/vendor of
        // their own) and written directly under their parent's row so the
        // sheet reads as one visual group per product.
        var parentIds = rows.Select(p => p.Id).ToList();
        var childRows = await _db.Products
            .AsNoTracking()
            .Where(c => c.ParentId != null && parentIds.Contains(c.ParentId!.Value))
            .Select(c => new
            {
                c.Id,
                c.Sku,
                ParentId = c.ParentId!.Value,
                c.Additional,
                NameEn = c.Flats.FirstOrDefault(f => f.Locale == "en")!.Name,
                Price = c.Flats.FirstOrDefault(f => f.Locale == "en")!.Price,
                SpecialPrice = c.Flats.FirstOrDefault(f => f.Locale == "en")!.SpecialPrice,
                MinQty = c.Flats.FirstOrDefault(f => f.Locale == "en")!.MinQty,
                MaxQty = c.Flats.FirstOrDefault(f => f.Locale == "en")!.MaxQty,
                Active = c.Flats.Any(f => f.Status == true),
                StockQty = c.Inventories.Sum(i => (int?)i.Qty) ?? 0,
            })
            .ToListAsync();
        var childrenByParent = childRows.GroupBy(c => c.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        var skuById = rows.ToDictionary(p => p.Id, p => p.Sku ?? "");

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Products");
        WriteHeaderRow(sheet);

        var r = 2;
        foreach (var row in rows)
        {
            var productVendorName = ProductService.ExtractVendorName(row.Additional, "en") ?? "";
            var matchedVendor = vendorByName.TryGetValue(productVendorName, out var mv) ? mv : null;
            var vendorNameTe = matchedVendor?.NameTe ?? ProductService.ExtractVendorName(row.Additional, "te");
            WriteRow(sheet, r++, new ProductRowValues(
                row.Id, row.Sku ?? "", row.NameEn ?? "", row.NameTe,
                row.CategoryId, row.CategoryName, row.CategoryNameTe,
                matchedVendor?.Id, productVendorName, vendorNameTe,
                row.Price ?? 0, row.SpecialPrice, row.StockQty, row.MinQty, row.MaxQty, row.Active,
                row.ShortDescriptionEn, row.ShortDescriptionTe, row.DescriptionEn, row.DescriptionTe));

            if (childrenByParent.TryGetValue(row.Id, out var children))
            {
                foreach (var child in children)
                {
                    var extras = ProductService.ReadChildVariantExtras(new Models.Catalog.Product { Additional = child.Additional });
                    WriteRow(sheet, r++, new ProductRowValues(
                        null, child.Sku ?? "", "", null,
                        null, null, null,
                        null, "", null,
                        child.Price ?? 0, child.SpecialPrice, child.StockQty, child.MinQty, child.MaxQty, child.Active,
                        null, null, null, null,
                        ParentSku: skuById.GetValueOrDefault(row.Id, ""),
                        VariantValue: !string.IsNullOrEmpty(extras.Value) ? extras.Value : child.NameEn));
                }
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        return ToBytes(workbook);
    }

    // ─── Template ────────────────────────────────────────────────────────

    /// <summary>Blank import template: headers + one worked example row, plus reference sheets of valid category IDs and existing vendor names so an admin filling it by hand doesn't have to guess.</summary>
    public async Task<byte[]> GenerateTemplateAsync()
    {
        using var workbook = new XLWorkbook();

        var sheet = workbook.Worksheets.Add("Products");
        WriteHeaderRow(sheet);
        WriteRow(sheet, 2, new ProductRowValues(
            null, "EXAMPLE-SKU-001", "Basmati Rice 1kg", "బాస్మతి రైస్ 1kg",
            null, "Rice & Grains", "బియ్యం & ధాన్యాలు",
            null, "Sri Lakshmi Traders", "శ్రీ లక్ష్మి ట్రేడర్స్",
            120.00m, 99.00m, 50, 1, 10, true,
            "Premium basmati rice", "ప్రీమియం బాస్మతి రైస్",
            "Long grain aged basmati rice, 1kg pack.", "పొడవైన బాస్మతి రైస్, 1kg ప్యాక్."));
        // Worked variant example — a second SKU sold under the same product
        // above. Leave SKU blank to auto-generate ("EXAMPLE-SKU-001-500g"),
        // or type your own. Category/Vendor/Name/Description are ignored for
        // a variant row (Parent SKU is filled in) — see the Instructions sheet.
        WriteRow(sheet, 3, new ProductRowValues(
            null, "", "", null,
            null, null, null,
            null, "", null,
            65.00m, null, 30, 1, null, true,
            null, null, null, null,
            ParentSku: "EXAMPLE-SKU-001", VariantValue: "500g"));
        // Second worked example — a brand-new standalone product with SKU
        // left blank on purpose, so it auto-generates from the top-level
        // vertical the Category belongs to (e.g. "FOODSTORE004668" for a
        // "Vegetables"-type category under Food Store) instead of you
        // having to invent one.
        WriteRow(sheet, 4, new ProductRowValues(
            null, "", "Tomato 1kg", "టమోటా 1kg",
            null, "Vegetables", "కూరగాయలు",
            null, "Sri Lakshmi Traders", "శ్రీ లక్ష్మి ట్రేడర్స్",
            40.00m, null, 100, 1, null, true,
            "Fresh tomatoes", "తాజా టమోటాలు",
            null, null));
        // Third worked example — a brand-new product THAT ALSO NEEDS VARIANTS,
        // with its own SKU still left blank to auto-generate. Since you won't
        // know that auto-generated SKU until after the import runs, you can't
        // type it into the variant row's Parent SKU — so row 5 gets a made-up
        // Row Ref ("TOM2", not a real SKU, never stored) that row 6 points at
        // instead. Use this pattern whenever a new product needs variants but
        // you still want its SKU auto-generated rather than typed by hand.
        WriteRow(sheet, 5, new ProductRowValues(
            null, "", "Cherry Tomatoes", "చెర్రీ టమోటాలు",
            null, "Vegetables", "కూరగాయలు",
            null, "Sri Lakshmi Traders", "శ్రీ లక్ష్మి ట్రేడర్స్",
            60.00m, null, 40, 1, null, true,
            "Fresh cherry tomatoes", "తాజా చెర్రీ టమోటాలు",
            null, null,
            RowRef: "TOM2"));
        WriteRow(sheet, 6, new ProductRowValues(
            null, "", "", null,
            null, null, null,
            null, "", null,
            35.00m, null, 20, 1, null, true,
            null, null, null, null,
            ParentSku: "TOM2", VariantValue: "250g"));
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        var instructions = workbook.Worksheets.Add("Instructions");
        WriteInstructions(instructions);

        var categoriesSheet = workbook.Worksheets.Add("Categories (reference)");
        categoriesSheet.Cell(1, 1).Value = "Category ID";
        categoriesSheet.Cell(1, 2).Value = "Category Path";
        categoriesSheet.Cell(1, 3).Value = "Category Name (Telugu)";
        categoriesSheet.Row(1).Style.Font.Bold = true;
        var catPaths = await _productService.GetCategoryPathsAsync();
        var cr = 2;
        foreach (var c in catPaths)
        {
            categoriesSheet.Cell(cr, 1).Value = c.Id;
            categoriesSheet.Cell(cr, 2).Value = c.Path;
            categoriesSheet.Cell(cr, 3).Value = c.NameTe ?? "";
            cr++;
        }
        categoriesSheet.SheetView.FreezeRows(1);
        categoriesSheet.Columns().AdjustToContents();

        var vendorsSheet = workbook.Worksheets.Add("Vendors (reference)");
        vendorsSheet.Cell(1, 1).Value = "Vendor ID";
        vendorsSheet.Cell(1, 2).Value = "Vendor Name";
        vendorsSheet.Cell(1, 3).Value = "Vendor Name (Telugu)";
        vendorsSheet.Row(1).Style.Font.Bold = true;
        var vendorRows = await _db.Vendors.Where(v => v.Active).OrderBy(v => v.Name).Select(v => new { v.Id, v.Name, v.NameTe }).ToListAsync();
        var vr = 2;
        foreach (var v in vendorRows)
        {
            vendorsSheet.Cell(vr, 1).Value = v.Id;
            vendorsSheet.Cell(vr, 2).Value = v.Name;
            vendorsSheet.Cell(vr, 3).Value = v.NameTe ?? "";
            vr++;
        }
        vendorsSheet.SheetView.FreezeRows(1);
        vendorsSheet.Columns().AdjustToContents();

        sheet.SetTabActive();
        return ToBytes(workbook);
    }

    private static void WriteInstructions(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "Column";
        sheet.Cell(1, 2).Value = "Notes";
        sheet.Row(1).Style.Font.Bold = true;

        var lines = new (string Column, string Note)[]
        {
            ("Product ID", "Leave blank for a new product. Fill in only to update an existing product (safest way to update — matches by ID, not by SKU)."),
            ("SKU", "Optional for a brand-new row (Product ID blank) — leave it blank to auto-generate one. For a normal product row, it matches this catalog's real convention: the TOP-LEVEL vertical your Category belongs to (Food Store, Build Store, Services, ...), continuing that vertical's real numbering — e.g. a \"Fruits & Vegetables\" row generates something like \"FOODSTORE004668\", right after the last real FOODSTORE SKU. For a variant row (Parent SKU filled in), it's generated from the Parent SKU + Variant Value instead (e.g. \"EXAMPLE-SKU-001-500g\"). Type your own instead if you want a specific SKU. Required when updating an existing row by Product ID only if you actually want to rename it — leave it blank to leave the SKU unchanged. If Product ID is blank and this SKU already exists, that existing product/variant is updated instead of a duplicate being created."),
            ("Parent SKU", "Leave blank for a normal, standalone product. Fill in a product's SKU here to make this row a VARIANT of that product instead (e.g. a \"500g\" and a \"1kg\" version of the same item, each with its own SKU/price/stock) — see the worked examples on rows 3 and 6. The parent can be an existing product, or a brand-new one defined earlier in this same file WITH AN EXPLICIT SKU (row 2's \"EXAMPLE-SKU-001\"). If the new parent's own SKU is left blank to auto-generate instead, you won't know it in time to type it here — use Row Ref instead (see rows 5-6's example) or the Product ID column. A Parent SKU that can't be resolved either way fails just that row. When Parent SKU is filled in, Category/Vendor/Name/Description columns are ignored — a variant belongs to its parent's category/vendor and its name is generated as \"<Parent Name> - <Variant Value>\"."),
            ("Row Ref", "Optional, and NOT a real SKU or stored anywhere — a temporary label you invent, used only to link rows together within this one file. Only needed on a brand-new parent row whose SKU you're leaving blank to auto-generate (so there's no real SKU yet to reference). Type any short text here (e.g. \"TOM2\"), then use that exact same text in Parent SKU on the variant row(s) below it — see rows 5-6. Must be unique within the file. Leave blank for every other row (you don't need it when typing an explicit SKU, or when the variant's parent already exists)."),
            ("Variant Value", "Required only for a variant row (Parent SKU filled in) — the option text shown to customers, e.g. \"500g\", \"1kg\", \"Red\". Ignored for a normal product row."),
            ("Name (English)", "Required for a normal product row. Ignored for a variant row — its name is generated from the parent's name + Variant Value."),
            ("Name (Telugu)", "Optional — falls back to the English name if left blank."),
            ("Category ID", "Preferred way to set the category — see the 'Categories (reference)' sheet for valid IDs. Takes priority over Category Name if both are filled in."),
            ("Category Name", "Used only when Category ID is blank. Category names can repeat under different parent categories, so an ID match is unambiguous — check the reference sheet if unsure."),
            ("Category Name (Telugu)", "Optional, export-only reference — shows the category's Telugu name. Importing never creates or edits a category, only links the product to one that already exists, so this column has no effect unless both Category ID and Category Name are blank (in which case it's used as a last-resort name match)."),
            ("Vendor ID", "Preferred way to set the vendor — see the 'Vendors (reference)' sheet for valid IDs. Takes priority over Vendor Name if both are filled in, and must match an existing vendor exactly (an unknown Vendor ID fails the row rather than creating a new vendor)."),
            ("Vendor Name", "Used only when Vendor ID is blank. Matched by exact name (not case-sensitive) — a misspelled name will silently create a new, duplicate vendor instead of matching the intended one, so prefer Vendor ID when updating an existing vendor's products. Created automatically if this vendor doesn't already exist."),
            ("Vendor Name (Telugu)", "Optional. Sets the Telugu name when a new vendor is created via Vendor Name. Also works as an update — filling this in for an EXISTING vendor (matched by Vendor ID or Vendor Name) corrects/backfills that vendor's Telugu name going forward. Leave blank to leave it unchanged."),
            ("Price", "Required for a product with no variants — that's the only price ever shown to customers for it. Optional for a product that has (or is getting) variants, since the app always shows the selected variant's own price instead and never the parent's — leave it blank there rather than typing a number nobody will see. On an update, leaving it blank always means \"don't change the existing price\", never \"clear it to 0\"."),
            ("Special Price", "Optional discounted price for any product, with or without variants. Leave blank for none."),
            ("Stock Qty", "Available stock quantity. Defaults to 0 if left blank — note this still matters even for a product with variants: it drives the product's overall \"in stock\" status (shown as an OUT OF STOCK overlay, and gates Add to Cart) independently of each variant's own stock."),
            ("Min Qty", "Minimum order quantity. Defaults to 1 if left blank."),
            ("Max Qty", "Maximum order quantity. Leave blank for no limit."),
            ("Active (Yes/No)", "Yes/No, True/False, or 1/0. Leave blank to default to Yes (active)."),
            ("Short Description (English/Telugu)", "Optional short summary shown on listing cards."),
            ("Description (English/Telugu)", "Optional full product description."),
        };
        var r = 2;
        foreach (var (col, note) in lines)
        {
            sheet.Cell(r, 1).Value = col;
            sheet.Cell(r, 2).Value = note;
            r++;
        }

        r += 1;
        sheet.Cell(r, 1).Value = "How import batching works";
        sheet.Cell(r, 1).Style.Font.Bold = true;
        r++;
        sheet.Cell(r, 1).Value =
            $"Rows are imported in batches of {BatchSize} (e.g. 100 rows import as 50 + 50). If a row turns out to be " +
            "invalid, only that row is skipped and reported below with its row number and reason — every other row, " +
            "in that batch and every other batch, still imports.";

        sheet.Columns().AdjustToContents();
        sheet.Column(2).Width = 90;
    }

    private static void WriteHeaderRow(IXLWorksheet sheet)
    {
        for (var i = 0; i < Headers.Length; i++)
            sheet.Cell(1, i + 1).Value = Headers[i];
        var headerRow = sheet.Row(1);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFEFEF");
    }

    private sealed record ProductRowValues(
        int? ProductId, string Sku, string NameEn, string? NameTe,
        int? CategoryId, string? CategoryName, string? CategoryNameTe,
        int? VendorId, string VendorName, string? VendorNameTe,
        decimal Price, decimal? SpecialPrice, int StockQty, int MinQty, int? MaxQty, bool Active,
        string? ShortDescriptionEn, string? ShortDescriptionTe, string? DescriptionEn, string? DescriptionTe,
        string? ParentSku = null, string? VariantValue = null, string? RowRef = null);

    private static void WriteRow(IXLWorksheet sheet, int row, ProductRowValues v)
    {
        if (v.ProductId.HasValue) sheet.Cell(row, 1).Value = v.ProductId.Value; else sheet.Cell(row, 1).Value = "";
        sheet.Cell(row, 2).Value = v.Sku;
        sheet.Cell(row, 3).Value = v.ParentSku ?? "";
        sheet.Cell(row, 4).Value = v.RowRef ?? "";
        sheet.Cell(row, 5).Value = v.VariantValue ?? "";
        sheet.Cell(row, 6).Value = v.NameEn;
        sheet.Cell(row, 7).Value = v.NameTe ?? "";
        if (v.CategoryId.HasValue) sheet.Cell(row, 8).Value = v.CategoryId.Value; else sheet.Cell(row, 8).Value = "";
        sheet.Cell(row, 9).Value = v.CategoryName ?? "";
        sheet.Cell(row, 10).Value = v.CategoryNameTe ?? "";
        if (v.VendorId.HasValue) sheet.Cell(row, 11).Value = v.VendorId.Value; else sheet.Cell(row, 11).Value = "";
        sheet.Cell(row, 12).Value = v.VendorName;
        sheet.Cell(row, 13).Value = v.VendorNameTe ?? "";
        sheet.Cell(row, 14).Value = v.Price;
        if (v.SpecialPrice.HasValue) sheet.Cell(row, 15).Value = v.SpecialPrice.Value; else sheet.Cell(row, 15).Value = "";
        sheet.Cell(row, 16).Value = v.StockQty;
        sheet.Cell(row, 17).Value = v.MinQty;
        if (v.MaxQty.HasValue) sheet.Cell(row, 18).Value = v.MaxQty.Value; else sheet.Cell(row, 18).Value = "";
        sheet.Cell(row, 19).Value = v.Active ? "Yes" : "No";
        sheet.Cell(row, 20).Value = v.ShortDescriptionEn ?? "";
        sheet.Cell(row, 21).Value = v.ShortDescriptionTe ?? "";
        sheet.Cell(row, 22).Value = v.DescriptionEn ?? "";
        sheet.Cell(row, 23).Value = v.DescriptionTe ?? "";
    }

    private static byte[] ToBytes(XLWorkbook workbook)
    {
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    // ─── Import ──────────────────────────────────────────────────────────

    private sealed class ParsedRow
    {
        public int RowNumber { get; init; }
        public int? ProductId { get; init; }
        public string Sku { get; init; } = "";
        public string? ParentSku { get; init; }
        public string? RowRef { get; init; }
        public string? VariantValue { get; init; }
        public string NameEn { get; init; } = "";
        public string? NameTe { get; init; }
        public int? CategoryId { get; init; }
        public string? CategoryName { get; init; }
        public string? CategoryNameTe { get; init; }
        public int? VendorId { get; init; }
        public string VendorName { get; init; } = "";
        public string? VendorNameTe { get; init; }
        public decimal? Price { get; init; }
        public decimal? SpecialPrice { get; init; }
        public int StockQty { get; init; }
        public int MinQty { get; init; } = 1;
        public int? MaxQty { get; init; }
        public bool Active { get; init; } = true;
        public string? ShortDescriptionEn { get; init; }
        public string? ShortDescriptionTe { get; init; }
        public string? DescriptionEn { get; init; }
        public string? DescriptionTe { get; init; }
    }

    private sealed class ImportContext
    {
        public required Dictionary<string, int> SkuToProductId { get; init; }
        // Maps a row's own "Row Ref" label (session-local, never a real SKU
        // or stored anywhere) to the real product it ended up creating —
        // lets a variant row link to a brand-new parent elsewhere in this
        // same file even when that parent's SKU is auto-generated and so
        // isn't known until the parent row is actually saved. See
        // StageVariantRowAsync's Parent SKU resolution and ImportBatchAsync.
        public required Dictionary<string, int> RowRefToProductId { get; init; }
        // Every value used in some variant row's Parent SKU column — either a
        // real SKU or a Row Ref label, whichever the admin used — computed
        // once upfront from the whole file. Lets a parent row's own
        // Price/Special Price go from required to optional the moment it's
        // clear this row will end up with variants: the storefront never
        // shows a variant-having product's own price (it always shows the
        // selected variant's price instead — see ProductService.GetPricingProduct
        // and the app's product_details_screen), so demanding a real price
        // for a row that's about to become variant-only parent product would
        // be enforcing data nobody will ever see.
        public required HashSet<string> ParentIdentifiersWithVariants { get; init; }
        public required Dictionary<string, Vendor> VendorByName { get; init; }
        // Only ever populated from already-persisted vendors (see ImportAsync) —
        // never mutated mid-import, unlike VendorByName, so it needs no
        // cleanup on a batch-retry.
        public required Dictionary<int, Vendor> VendorById { get; init; }
        public required HashSet<string> UsedUrlKeys { get; init; }
        public required int InventorySourceId { get; init; }
    }

    private sealed class StagedRow
    {
        public required ParsedRow Row { get; init; }
        public required bool Created { get; init; }
        // For an update (Created == false): whether any field actually
        // differed from what was already stored — false means this row was
        // a no-op re-import and is reported as "unchanged", not "updated".
        // Always true for a create.
        public required bool Changed { get; init; }
        public required Product Product { get; init; }
    }

    /// <summary>
    /// Parses the uploaded workbook and imports every data row in chunks of
    /// <see cref="BatchSize"/>. Each chunk is saved in one round trip when
    /// possible; if a chunk's save fails outright (e.g. one bad row causes a
    /// DB constraint error), that chunk alone is retried row-by-row so the
    /// other 49 rows in it still land instead of the whole chunk being lost.
    /// </summary>
    public async Task<ImportProductsResponse> ImportAsync(Stream fileStream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(fileStream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read the uploaded file — please make sure it's a valid .xlsx file exported from this system or the downloaded import template.", ex);
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name.Equals("Products", StringComparison.OrdinalIgnoreCase))
                ?? workbook.Worksheet(1);

            var headerRow = sheet.Row(1);
            var lastHeaderCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
            var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var c = 1; c <= lastHeaderCol; c++)
            {
                var header = headerRow.Cell(c).GetString().Trim();
                if (!string.IsNullOrEmpty(header)) columnIndex[header] = c;
            }

            int? Col(string name) => columnIndex.TryGetValue(name, out var i) ? i : (int?)null;

            var required = new[] { "SKU", "Name (English)", "Price" };
            var missing = required.Where(h => Col(h) == null).ToList();
            if (Col("Category ID") == null && Col("Category Name") == null)
                missing.Add("Category ID or Category Name");
            if (Col("Vendor ID") == null && Col("Vendor Name") == null)
                missing.Add("Vendor ID or Vendor Name");
            if (missing.Count > 0)
                throw new InvalidOperationException($"The uploaded file is missing required column(s): {string.Join(", ", missing)}. Please use the provided import template.");

            var colProductId = Col("Product ID");
            var colSku = Col("SKU")!.Value;
            var colParentSku = Col("Parent SKU");
            var colRowRef = Col("Row Ref");
            var colVariantValue = Col("Variant Value");
            var colNameEn = Col("Name (English)")!.Value;
            var colNameTe = Col("Name (Telugu)");
            var colCategoryId = Col("Category ID");
            var colCategoryName = Col("Category Name");
            var colCategoryNameTe = Col("Category Name (Telugu)");
            var colVendorId = Col("Vendor ID");
            var colVendorName = Col("Vendor Name");
            var colVendorNameTe = Col("Vendor Name (Telugu)");
            var colPrice = Col("Price")!.Value;
            var colSpecialPrice = Col("Special Price");
            var colStockQty = Col("Stock Qty");
            var colMinQty = Col("Min Qty");
            var colMaxQty = Col("Max Qty");
            var colActive = Col("Active (Yes/No)");
            var colShortDescEn = Col("Short Description (English)");
            var colShortDescTe = Col("Short Description (Telugu)");
            var colDescEn = Col("Description (English)");
            var colDescTe = Col("Description (Telugu)");

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            var parsedRows = new List<ParsedRow>();

            for (var r = 2; r <= lastRow; r++)
            {
                var row = sheet.Row(r);
                if (row.IsEmpty()) continue;

                string GetString(int? col)
                {
                    if (!col.HasValue) return "";
                    return row.Cell(col.Value).GetString().Trim();
                }

                decimal? GetDecimal(int? col)
                {
                    if (!col.HasValue) return null;
                    var cell = row.Cell(col.Value);
                    if (cell.IsEmpty()) return null;
                    if (cell.TryGetValue<decimal>(out var d)) return d;
                    var s = cell.GetString().Trim();
                    return decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d2) ? d2 : null;
                }

                int? GetInt(int? col)
                {
                    var d = GetDecimal(col);
                    return d.HasValue ? (int)Math.Round(d.Value) : null;
                }

                bool? GetBool(int? col)
                {
                    var s = GetString(col).ToLowerInvariant();
                    return string.IsNullOrEmpty(s) ? null : s is "yes" or "true" or "1" or "active";
                }

                var sku = GetString(colSku);
                var nameEn = GetString(colNameEn);
                var parentSku = colParentSku.HasValue ? GetString(colParentSku) : "";
                var variantValue = colVariantValue.HasValue ? GetString(colVariantValue) : "";
                // A variant row legitimately has blank SKU (auto-generated) and
                // blank Name (English) (derived from the parent) — only treat a
                // row as stray/blank when it has none of the identifying fields.
                if (string.IsNullOrWhiteSpace(sku) && string.IsNullOrWhiteSpace(nameEn)
                    && string.IsNullOrWhiteSpace(parentSku) && string.IsNullOrWhiteSpace(variantValue))
                    continue; // stray blank row with only whitespace/formatting

                parsedRows.Add(new ParsedRow
                {
                    RowNumber = r,
                    ProductId = GetInt(colProductId),
                    Sku = sku,
                    ParentSku = string.IsNullOrWhiteSpace(parentSku) ? null : parentSku,
                    RowRef = colRowRef.HasValue && !string.IsNullOrWhiteSpace(GetString(colRowRef)) ? GetString(colRowRef).Trim() : null,
                    VariantValue = string.IsNullOrWhiteSpace(variantValue) ? null : variantValue,
                    NameEn = nameEn,
                    NameTe = colNameTe.HasValue ? GetString(colNameTe) : null,
                    CategoryId = GetInt(colCategoryId),
                    CategoryName = colCategoryName.HasValue ? GetString(colCategoryName) : null,
                    CategoryNameTe = colCategoryNameTe.HasValue ? GetString(colCategoryNameTe) : null,
                    VendorId = GetInt(colVendorId),
                    VendorName = GetString(colVendorName),
                    VendorNameTe = colVendorNameTe.HasValue ? GetString(colVendorNameTe) : null,
                    Price = GetDecimal(colPrice),
                    SpecialPrice = GetDecimal(colSpecialPrice),
                    StockQty = GetInt(colStockQty) ?? 0,
                    MinQty = GetInt(colMinQty) ?? 1,
                    MaxQty = GetInt(colMaxQty),
                    Active = GetBool(colActive) ?? true,
                    ShortDescriptionEn = colShortDescEn.HasValue ? GetString(colShortDescEn) : null,
                    ShortDescriptionTe = colShortDescTe.HasValue ? GetString(colShortDescTe) : null,
                    DescriptionEn = colDescEn.HasValue ? GetString(colDescEn) : null,
                    DescriptionTe = colDescTe.HasValue ? GetString(colDescTe) : null,
                });
            }

            var result = new ImportProductsResponse { TotalRows = parsedRows.Count };
            if (parsedRows.Count == 0)
            {
                result.Message = "No data rows found in the uploaded file.";
                return result;
            }

            // Two rows can't claim the same Row Ref — it would be ambiguous
            // which one a variant row meant to link to. Caught here, before
            // anything is staged, so a duplicate fails cleanly instead of
            // leaving an orphaned product behind.
            var seenRowRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<ParsedRow>(parsedRows.Count);
            foreach (var row in parsedRows)
            {
                if (row.RowRef != null && !seenRowRefs.Add(row.RowRef))
                {
                    result.FailedCount++;
                    result.Errors.Add(new ImportProductRowError { Row = row.RowNumber, Sku = row.Sku, Message = $"Row Ref '{row.RowRef}' is used by more than one row — each Row Ref must be unique within the file." });
                    continue;
                }
                deduped.Add(row);
            }
            parsedRows = deduped;

            var inventorySourceId = await _db.InventorySources.Select(s => s.Id).FirstOrDefaultAsync();
            if (inventorySourceId == 0) inventorySourceId = 1;

            // Two separate batch-loops, not one reordered loop: ImportBatchAsync
            // stages every row in a batch BEFORE its single SaveChangesAsync
            // call, so simply sorting variant rows after normal rows isn't
            // enough on its own — a small file (like a normal row + its own
            // new variant) would still land both in the same batch, staged
            // together, with the variant resolving its Parent SKU before the
            // parent has an ID. Running normal rows to completion first (own
            // batches, own commits) guarantees ctx.SkuToProductId has every
            // parent's real ID — including a brand-new one defined earlier in
            // this same file — before any variant row is even staged.
            // RowNumber is kept from the original file for error messages, so
            // this reordering doesn't affect what a user sees for a failed row.
            var normalRows = parsedRows.Where(r => string.IsNullOrWhiteSpace(r.ParentSku)).ToList();
            var variantRows = parsedRows.Where(r => !string.IsNullOrWhiteSpace(r.ParentSku)).ToList();

            var allVendors = await _db.Vendors.ToListAsync();
            var ctx = new ImportContext
            {
                SkuToProductId = await _db.Products.Where(p => p.Sku != null).ToDictionaryAsync(p => p.Sku!, p => p.Id),
                RowRefToProductId = new Dictionary<string, int>(),
                ParentIdentifiersWithVariants = new HashSet<string>(
                    variantRows.Select(r => r.ParentSku!), StringComparer.OrdinalIgnoreCase),
                VendorByName = allVendors.ToDictionary(v => v.Name, v => v, StringComparer.OrdinalIgnoreCase),
                VendorById = allVendors.ToDictionary(v => v.Id, v => v),
                UsedUrlKeys = new HashSet<string>(),
                InventorySourceId = inventorySourceId
            };

            foreach (var batch in Chunk(normalRows, BatchSize))
                await ImportBatchAsync(batch, ctx, result);
            foreach (var batch in Chunk(variantRows, BatchSize))
                await ImportBatchAsync(batch, ctx, result);

            var summaryParts = new List<string> { $"{result.CreatedCount} created", $"{result.UpdatedCount} updated" };
            if (result.UnchangedCount > 0) summaryParts.Add($"{result.UnchangedCount} unchanged");
            if (result.FailedCount > 0) summaryParts.Add($"{result.FailedCount} failed");
            result.Message = result.FailedCount == 0
                ? $"Imported {result.SuccessCount} of {result.TotalRows} product(s) — {string.Join(", ", summaryParts)}."
                : $"Imported {result.SuccessCount} of {result.TotalRows} product(s) — {string.Join(", ", summaryParts)}. See errors for details.";
            return result;
        }
    }

    private static IEnumerable<List<ParsedRow>> Chunk(List<ParsedRow> rows, int size)
    {
        for (var i = 0; i < rows.Count; i += size)
            yield return rows.GetRange(i, Math.Min(size, rows.Count - i));
    }

    private async Task ImportBatchAsync(List<ParsedRow> batch, ImportContext ctx, ImportProductsResponse result)
    {
        var staged = new List<StagedRow>();
        foreach (var row in batch)
        {
            var (ok, stagedRow, error) = await StageRowAsync(row, ctx);
            if (!ok)
            {
                result.FailedCount++;
                result.Errors.Add(new ImportProductRowError { Row = row.RowNumber, Sku = row.Sku, Message = error! });
                continue;
            }
            staged.Add(stagedRow!);
        }

        if (staged.Count == 0) return;

        try
        {
            await _db.SaveChangesAsync();
            foreach (var s in staged)
            {
                // Keyed by the actual saved SKU, not s.Row.Sku — a variant row
                // with a blank SKU column gets one auto-generated in StageRowAsync,
                // so the row's own (blank) value would never match anything.
                ctx.SkuToProductId[s.Product.Sku] = s.Product.Id;
                if (s.Row.RowRef != null) ctx.RowRefToProductId[s.Row.RowRef] = s.Product.Id;
                result.SuccessCount++;
                if (s.Created) result.CreatedCount++;
                else if (s.Changed) result.UpdatedCount++;
                else result.UnchangedCount++;
            }

            // A large file imports as many of these batches in a row, all
            // against the SAME DbContext — without this, every earlier
            // batch's ~5 entities-per-product stay tracked for the rest of
            // the whole import. Confirmed empirically: importing 100 new
            // products as two 50-row batches, the first batch (DbContext
            // still small) always succeeded, but the second — now with the
            // first batch's ~250 entities still sitting in the tracker —
            // started corrupting Pomelo's generated-key propagation for
            // roughly every other row. Clearing after each successful save
            // keeps the tracker bounded to one batch's worth at a time.
            _db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Product import batch (rows {First}-{Last}) failed as a whole; retrying row-by-row", batch.First().RowNumber, batch.Last().RowNumber);

            _db.ChangeTracker.Clear();
            // Anything this batch staged as a brand-new (unsaved) vendor is
            // now detached and invalid — drop it so the retry cleanly
            // re-creates it per row instead of reusing a dead reference.
            foreach (var key in ctx.VendorByName.Where(kv => kv.Value.Id == 0).Select(kv => kv.Key).ToList())
                ctx.VendorByName.Remove(key);

            foreach (var s in staged)
            {
                var (ok, stagedRow, error) = await StageRowAsync(s.Row, ctx);
                if (!ok)
                {
                    result.FailedCount++;
                    result.Errors.Add(new ImportProductRowError { Row = s.Row.RowNumber, Sku = s.Row.Sku, Message = error! });
                    continue;
                }
                try
                {
                    await _db.SaveChangesAsync();
                    ctx.SkuToProductId[stagedRow!.Product.Sku] = stagedRow.Product.Id;
                    if (stagedRow.Row.RowRef != null) ctx.RowRefToProductId[stagedRow.Row.RowRef] = stagedRow.Product.Id;
                    result.SuccessCount++;
                    if (stagedRow.Created) result.CreatedCount++;
                    else if (stagedRow.Changed) result.UpdatedCount++;
                    else result.UnchangedCount++;
                }
                catch (Exception rowEx)
                {
                    _db.ChangeTracker.Clear();
                    foreach (var key in ctx.VendorByName.Where(kv => kv.Value.Id == 0).Select(kv => kv.Key).ToList())
                        ctx.VendorByName.Remove(key);
                    result.FailedCount++;
                    result.Errors.Add(new ImportProductRowError { Row = s.Row.RowNumber, Sku = s.Row.Sku, Message = CleanErrorMessage(rowEx) });
                }
            }
        }
    }

    private Task<(bool Ok, StagedRow? Staged, string? Error)> StageRowAsync(ParsedRow row, ImportContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(row.ParentSku))
            return StageVariantRowAsync(row, ctx);
        return StageProductRowAsync(row, ctx);
    }

    private async Task<(bool Ok, StagedRow? Staged, string? Error)> StageProductRowAsync(ParsedRow row, ImportContext ctx)
    {
        // SKU itself is NOT required for a brand-new product (Product ID and
        // SKU both blank) — see the auto-generation in the create branch
        // below, which derives one from the category instead. It's still
        // required to update an existing product by Product ID, since a
        // blank value there must never be silently treated as "clear the
        // SKU" or "rename to blank" — see the guards further down.
        if (string.IsNullOrWhiteSpace(row.NameEn))
            return (false, null, "Name (English) is required");
        if (!row.VendorId.HasValue && string.IsNullOrWhiteSpace(row.VendorName))
            return (false, null, "Vendor ID or Vendor Name is required");
        // Whether Price is actually required depends on whether this row has
        // (or will have) variants — resolved further down, once we know if
        // it's a create/update and can check for existing children. A given
        // Price still always has to be sane, though.
        if (row.Price.HasValue && row.Price < 0)
            return (false, null, "Price must be >= 0");
        if (!row.CategoryId.HasValue && string.IsNullOrWhiteSpace(row.CategoryName) && string.IsNullOrWhiteSpace(row.CategoryNameTe))
            return (false, null, "Category ID or Category Name is required");

        // Category Name (Telugu) is a last-resort name match when both
        // Category ID and Category Name (English) are blank — ResolveCategoryAsync
        // matches a name against any locale's translation, not just English.
        var (category, categoryError) = await _productService.ResolveCategoryAsync(
            row.CategoryId,
            !string.IsNullOrWhiteSpace(row.CategoryName) ? row.CategoryName : row.CategoryNameTe);
        if (categoryError != null) return (false, null, categoryError);

        // Vendor ID is the unambiguous match — it must name an EXISTING
        // vendor exactly (no auto-create), so a fat-fingered id fails the
        // row loudly instead of quietly doing the wrong thing. Vendor Name
        // is the fallback for admins who don't know the id yet; it still
        // auto-creates a new vendor when the name doesn't match one on file
        // — which is also exactly how a misspelled name silently creates a
        // duplicate vendor instead of matching the intended one, so Vendor
        // ID should be preferred whenever the vendor already exists.
        Vendor vendor;
        string vendorNameTrimmed;
        if (row.VendorId.HasValue)
        {
            if (!ctx.VendorById.TryGetValue(row.VendorId.Value, out vendor!))
                return (false, null, $"Vendor ID {row.VendorId} not found");
            vendorNameTrimmed = vendor.Name;
        }
        else
        {
            vendorNameTrimmed = row.VendorName.Trim();
            if (!ctx.VendorByName.TryGetValue(vendorNameTrimmed, out vendor!))
            {
                vendor = new Vendor
                {
                    Name = vendorNameTrimmed,
                    NameTe = vendorNameTrimmed,
                    Active = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Vendors.Add(vendor);
                ctx.VendorByName[vendorNameTrimmed] = vendor;
            }
        }

        // A Telugu name in the sheet backfills/updates the vendor's own
        // NameTe — works whether the vendor was just matched (by ID or
        // name) or freshly created above (which otherwise defaults NameTe
        // to the English name). Blank means "leave it unchanged".
        if (!string.IsNullOrWhiteSpace(row.VendorNameTe))
            vendor.NameTe = row.VendorNameTe!.Trim();

        var now = DateTime.UtcNow;
        var vendorNameTeResolved = vendor.NameTe ?? vendorNameTrimmed;

        // Product ID (when given) is the safest match — a re-imported export
        // round-trips unambiguously even if the SKU column was hand-edited.
        // Falls back to an existing SKU match, then finally a brand-new row.
        // existingId == 0 means the SKU is only reserved (sentinel — another
        // row in THIS batch claimed it, see the create branch below) rather
        // than a real, already-saved product — that's not something to
        // update, it falls through to the create branch instead, where it
        // gets rejected with a clear "already exists" error.
        int? targetId = row.ProductId;
        if (!targetId.HasValue && !string.IsNullOrWhiteSpace(row.Sku) && ctx.SkuToProductId.TryGetValue(row.Sku, out var existingId) && existingId != 0)
            targetId = existingId;

        Product? product = null;
        if (targetId.HasValue)
        {
            product = await _db.Products
                .Include(p => p.Flats)
                .Include(p => p.Inventories)
                .Include(p => p.Categories)
                .FirstOrDefaultAsync(p => p.Id == targetId.Value);
            if (product == null)
                return (false, null, $"Product ID {targetId} not found");
        }

        // A product with variants never shows its own price/stock to a
        // customer — the storefront always defers to whichever variant is
        // selected (see ProductService.GetPricingProduct and the app's
        // product detail screen) — so Price is only actually required for a
        // row that will stay a plain, variant-less product. "Will have
        // variants" covers both a brand-new parent getting variant rows
        // later in this same file (via its SKU or Row Ref, precomputed in
        // ImportAsync) and an existing product that already has children.
        var willHaveVariantsInThisFile =
            (!string.IsNullOrWhiteSpace(row.Sku) && ctx.ParentIdentifiersWithVariants.Contains(row.Sku)) ||
            (!string.IsNullOrWhiteSpace(row.RowRef) && ctx.ParentIdentifiersWithVariants.Contains(row.RowRef));
        var alreadyHasVariants = product != null && await _db.Products.AnyAsync(c => c.ParentId == product.Id);
        var hasVariants = willHaveVariantsInThisFile || alreadyHasVariants;

        if (!hasVariants && !row.Price.HasValue)
            return (false, null, "Price is required for a product with no variants");

        var created = product == null;
        if (created)
        {
            string sku;
            if (!string.IsNullOrWhiteSpace(row.Sku))
            {
                sku = row.Sku;
                if (ctx.SkuToProductId.ContainsKey(sku))
                    return (false, null, $"A product with SKU '{sku}' already exists (fill in its Product ID column to update it instead)");
            }
            else
            {
                // No admin API in this app auto-generates a SKU for a brand-new
                // product either (Create Product requires one explicitly too) —
                // this is a new convenience specific to bulk import, where
                // inventing hundreds of SKUs by hand isn't realistic. Matches
                // this catalog's real existing convention exactly: the whole
                // bulk-imported catalog is SKU'd as "<TOP-LEVEL VERTICAL><6-digit
                // sequence>" (e.g. FOODSTORE004667) — the prefix always traces to
                // one of a handful of top-level categories (Food Store, Build
                // Store, Services, ...), NOT the specific leaf/sub-category a row
                // picks (e.g. "Fruits & Vegetables" is filed under "Food Store").
                // See GetTopLevelCategoryNameAsync / NextSkuInVerticalAsync.
                var verticalName = await GetTopLevelCategoryNameAsync(category!);
                var prefix = string.IsNullOrWhiteSpace(verticalName) ? "PRODUCT" : Slugify(verticalName).Replace("-", "").ToUpperInvariant();
                sku = await NextSkuInVerticalAsync(prefix, ctx);
            }

            // Reserve it immediately (sentinel id 0, overwritten with the
            // real id once this batch actually saves — see ImportBatchAsync)
            // — ctx.SkuToProductId otherwise isn't updated until after the
            // whole batch's single SaveChangesAsync, so two rows landing in
            // the SAME batch (explicit SKU typo'd the same on both, or two
            // auto-generated ones computed before either had saved) would
            // both sail past the ContainsKey checks above and try to insert
            // the same SKU together — which doesn't fail cleanly, it corrupts
            // EF's own generated-key tracking for the whole batch instead.
            ctx.SkuToProductId[sku] = 0;

            var urlKeyBase = Slugify(row.NameEn);
            if (string.IsNullOrEmpty(urlKeyBase)) urlKeyBase = Slugify(sku);
            if (string.IsNullOrEmpty(urlKeyBase)) urlKeyBase = "product";
            var urlKey = urlKeyBase;
            var suffix = 1;
            while (ctx.UsedUrlKeys.Contains(urlKey) || await _db.ProductFlats.AnyAsync(f => f.UrlKey == urlKey))
                urlKey = $"{urlKeyBase}-{row.RowNumber}-{suffix++}";
            ctx.UsedUrlKeys.Add(urlKey);

            product = new Product
            {
                Sku = sku,
                Type = "simple",
                Additional = BuildAdditionalJson(null, vendorNameTrimmed, vendorNameTeResolved),
                CreatedAt = now,
                UpdatedAt = now
            };

            var nameTeNew = !string.IsNullOrWhiteSpace(row.NameTe) ? row.NameTe!.Trim() : row.NameEn;
            var shortDescTeNew = !string.IsNullOrWhiteSpace(row.ShortDescriptionTe) ? row.ShortDescriptionTe : row.ShortDescriptionEn;
            var descTeNew = !string.IsNullOrWhiteSpace(row.DescriptionTe) ? row.DescriptionTe : row.DescriptionEn;

            product.Flats.Add(new ProductFlat
            {
                Sku = sku,
                Type = "simple",
                Name = row.NameEn,
                UrlKey = urlKey,
                Price = row.Price ?? 0,
                SpecialPrice = row.SpecialPrice,
                Status = row.Active,
                ShortDescription = row.ShortDescriptionEn,
                Description = row.DescriptionEn,
                MinQty = row.MinQty,
                MaxQty = row.MaxQty,
                Locale = "en",
                Channel = "default",
                VisibleIndividually = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            product.Flats.Add(new ProductFlat
            {
                Sku = sku,
                Type = "simple",
                Name = nameTeNew,
                UrlKey = urlKey,
                Price = row.Price ?? 0,
                SpecialPrice = row.SpecialPrice,
                Status = row.Active,
                ShortDescription = shortDescTeNew,
                Description = descTeNew,
                MinQty = row.MinQty,
                MaxQty = row.MaxQty,
                Locale = "te",
                Channel = "default",
                VisibleIndividually = true,
                CreatedAt = now,
                UpdatedAt = now
            });

            product.Inventories.Add(new ProductInventory { Qty = row.StockQty, InventorySourceId = ctx.InventorySourceId });
            product.Categories.Add(category!);

            var effectivePrice = row.SpecialPrice.HasValue && row.SpecialPrice > 0 ? row.SpecialPrice.Value : (row.Price ?? 0);
            product.PriceIndices.Add(new ProductPriceIndex
            {
                MinPrice = effectivePrice,
                RegularMinPrice = row.Price ?? 0,
                MaxPrice = effectivePrice,
                RegularMaxPrice = row.Price ?? 0,
                ChannelId = 1,
                CreatedAt = now,
                UpdatedAt = now
            });

            _db.Products.Add(product);
        }
        else
        {
            // Tracks whether this row's values actually differ from what's
            // already in the DB — a re-imported file where most rows are
            // untouched shouldn't report (or write) those rows as "updated".
            // UpdatedAt is only bumped for entities that genuinely changed,
            // so an unchanged row also produces no real DB write: EF's own
            // dirty-tracking sees no modified properties and skips it.
            var changed = false;

            // A blank SKU here means "leave it as-is" (matches by Product ID,
            // so there's no ambiguity to resolve) — never treat it as "rename
            // to blank".
            if (!string.IsNullOrWhiteSpace(row.Sku) && row.Sku != product!.Sku)
            {
                if (ctx.SkuToProductId.TryGetValue(row.Sku, out var conflictId) && conflictId != product.Id)
                    return (false, null, $"Cannot rename to SKU '{row.Sku}' — already used by product ID {conflictId}");
                product.Sku = row.Sku;
                changed = true;
            }

            var flatEn = product.Flats.FirstOrDefault(f => f.Locale == "en") ?? product.Flats.FirstOrDefault();
            var flatEnChanged = false;
            if (flatEn != null)
            {
                flatEnChanged |= SetIfDifferent(v => flatEn.Sku = v, flatEn.Sku, product.Sku);
                flatEnChanged |= SetIfDifferent(v => flatEn.Name = v, flatEn.Name, row.NameEn);
                // Blank Price on an update means "leave it as-is" — same
                // convention as a blank SKU — not "reset to 0".
                flatEnChanged |= SetIfDifferent(v => flatEn.Price = v, flatEn.Price, row.Price ?? flatEn.Price);
                flatEnChanged |= SetIfDifferent(v => flatEn.SpecialPrice = v, flatEn.SpecialPrice, row.SpecialPrice);
                flatEnChanged |= SetIfDifferent(v => flatEn.ShortDescription = v, flatEn.ShortDescription, row.ShortDescriptionEn);
                flatEnChanged |= SetIfDifferent(v => flatEn.Description = v, flatEn.Description, row.DescriptionEn);
                flatEnChanged |= SetIfDifferent(v => flatEn.MinQty = v, flatEn.MinQty, row.MinQty);
                flatEnChanged |= SetIfDifferent(v => flatEn.MaxQty = v, flatEn.MaxQty, row.MaxQty);
            }

            var statusChanged = false;
            foreach (var f in product.Flats)
                statusChanged |= SetIfDifferent(v => f.Status = v, f.Status, row.Active);

            var teFlat = product.Flats.FirstOrDefault(f => f.Locale == "te");
            var nameTe = !string.IsNullOrWhiteSpace(row.NameTe) ? row.NameTe!.Trim() : row.NameEn;
            var shortDescTe = !string.IsNullOrWhiteSpace(row.ShortDescriptionTe) ? row.ShortDescriptionTe : (row.ShortDescriptionEn ?? teFlat?.ShortDescription);
            var descTe = !string.IsNullOrWhiteSpace(row.DescriptionTe) ? row.DescriptionTe : (row.DescriptionEn ?? teFlat?.Description);
            var teFlatChanged = false;
            if (teFlat != null)
            {
                teFlatChanged |= SetIfDifferent(v => teFlat.Sku = v, teFlat.Sku, product.Sku);
                teFlatChanged |= SetIfDifferent(v => teFlat.Name = v, teFlat.Name, nameTe);
                teFlatChanged |= SetIfDifferent(v => teFlat.Price = v, teFlat.Price, row.Price ?? teFlat.Price);
                teFlatChanged |= SetIfDifferent(v => teFlat.SpecialPrice = v, teFlat.SpecialPrice, row.SpecialPrice);
                teFlatChanged |= SetIfDifferent(v => teFlat.ShortDescription = v, teFlat.ShortDescription, shortDescTe);
                teFlatChanged |= SetIfDifferent(v => teFlat.Description = v, teFlat.Description, descTe);
                teFlatChanged |= SetIfDifferent(v => teFlat.MinQty = v, teFlat.MinQty, row.MinQty);
                teFlatChanged |= SetIfDifferent(v => teFlat.MaxQty = v, teFlat.MaxQty, row.MaxQty);
            }
            else if (flatEn != null)
            {
                product.Flats.Add(new ProductFlat
                {
                    Sku = product.Sku,
                    Type = "simple",
                    Name = nameTe,
                    UrlKey = flatEn.UrlKey,
                    Price = row.Price ?? flatEn.Price,
                    SpecialPrice = row.SpecialPrice,
                    Status = row.Active,
                    ShortDescription = shortDescTe,
                    Description = descTe,
                    MinQty = row.MinQty,
                    MaxQty = row.MaxQty,
                    Locale = "te",
                    Channel = "default",
                    VisibleIndividually = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                teFlatChanged = true; // a brand-new row is unambiguously a change
            }

            if (flatEn != null && (flatEnChanged || statusChanged)) flatEn.UpdatedAt = now;
            if (teFlat != null && (teFlatChanged || statusChanged)) teFlat.UpdatedAt = now;
            changed = changed || flatEnChanged || statusChanged || teFlatChanged;

            var inventory = product.Inventories.FirstOrDefault();
            if (inventory != null)
                changed |= SetIfDifferent(v => inventory.Qty = v, inventory.Qty, row.StockQty);
            else
            {
                product.Inventories.Add(new ProductInventory { Qty = row.StockQty, InventorySourceId = ctx.InventorySourceId });
                changed = true; // going from "no inventory row" to an explicit one is a real state change
            }

            var currentCategoryId = product.Categories.Count == 1 ? product.Categories.First().Id : (int?)null;
            if (currentCategoryId != category!.Id)
            {
                product.Categories.Clear();
                product.Categories.Add(category);
                changed = true;
            }

            // Compare the actual vendor text, not the raw JSON string — scraped
            // Additional blobs often carry other keys (site, seeder, source_url,
            // …) that a naive re-serialize would silently drop, and even a
            // genuinely unchanged vendor can be stored with different JSON
            // whitespace than JsonSerializer produces, which would falsely
            // flag every row as "changed". BuildAdditionalJson merges into
            // the existing object instead of replacing it.
            var existingVendorEn = ProductService.ExtractVendorName(product.Additional, "en");
            var existingVendorTe = ProductService.ExtractVendorName(product.Additional, "te");
            if (!string.Equals(existingVendorEn, vendorNameTrimmed, StringComparison.Ordinal) ||
                !string.Equals(existingVendorTe, vendorNameTeResolved, StringComparison.Ordinal))
            {
                product.Additional = BuildAdditionalJson(product.Additional, vendorNameTrimmed, vendorNameTeResolved);
                changed = true;
            }

            if (changed) product.UpdatedAt = now;

            return (true, new StagedRow { Row = row, Created = false, Changed = changed, Product = product }, null);
        }

        return (true, new StagedRow { Row = row, Created = created, Changed = true, Product = product }, null);
    }

    /// <summary>
    /// Stages a variant row (Parent SKU filled in) — creates or updates a
    /// real child Product row (ParentId = the resolved parent's id),
    /// mirroring AdminGlobalProductsController.CreateVariant's exact data
    /// shape so a variant created via import is indistinguishable from one
    /// created via the admin "Add Variant" API. Category/Vendor/Name/
    /// Description are intentionally never read from the row — a variant
    /// belongs to its parent's category/vendor, and its name is always
    /// "&lt;Parent Name&gt; - &lt;Variant Value&gt;".
    /// </summary>
    private async Task<(bool Ok, StagedRow? Staged, string? Error)> StageVariantRowAsync(ParsedRow row, ImportContext ctx)
    {
        if (string.IsNullOrWhiteSpace(row.VariantValue))
            return (false, null, "Variant Value is required for a variant row (Parent SKU is filled in)");
        if (!row.Price.HasValue || row.Price < 0)
            return (false, null, "Price is required and must be >= 0");

        // Resolved from ctx, not a fresh DB query — by the time any variant
        // row runs, every normal row (including a brand-new parent defined
        // earlier in this same file) has already been imported and saved;
        // see the reordering in ImportAsync. Tries a real SKU first, then
        // falls back to a Row Ref — needed when the parent is brand-new AND
        // its own SKU was left blank to auto-generate, since there's then no
        // real SKU value the admin could have known to type into Parent SKU
        // when filling out the sheet; Row Ref lets them link to it by a
        // label they invented themselves instead (see ImportContext.RowRefToProductId).
        if (!ctx.SkuToProductId.TryGetValue(row.ParentSku!, out var parentId)
            && !ctx.RowRefToProductId.TryGetValue(row.ParentSku!, out parentId))
            return (false, null, $"Parent SKU '{row.ParentSku}' not found — add its row earlier in the file (or reference it there by Row Ref if its SKU is auto-generated), or make sure it already exists");

        var parent = await _db.Products.Include(p => p.Flats).FirstOrDefaultAsync(p => p.Id == parentId);
        if (parent == null)
            return (false, null, $"Parent SKU '{row.ParentSku}' not found");
        if (parent.ParentId != null)
            return (false, null, $"Parent SKU '{row.ParentSku}' is itself a variant — variants can't have their own sub-variants");

        var value = row.VariantValue!.Trim();
        var now = DateTime.UtcNow;

        // Same match precedence as a normal row: Product ID first, then SKU.
        // existingId == 0 is a same-batch reservation, not a real product —
        // see the equivalent guard in StageProductRowAsync.
        int? targetId = row.ProductId;
        if (!targetId.HasValue && !string.IsNullOrWhiteSpace(row.Sku) && ctx.SkuToProductId.TryGetValue(row.Sku, out var existingId) && existingId != 0)
            targetId = existingId;

        Product? child = null;
        if (targetId.HasValue)
        {
            child = await _db.Products
                .Include(p => p.Flats)
                .Include(p => p.Inventories)
                .FirstOrDefaultAsync(p => p.Id == targetId.Value);
            if (child == null)
                return (false, null, $"Product ID {targetId} not found");
            if (child.ParentId != parent.Id)
                return (false, null, $"Product ID {targetId} is not a variant of parent SKU '{row.ParentSku}' — moving a variant to a different parent isn't supported by import");
        }

        var created = child == null;
        if (created)
        {
            if (!string.IsNullOrWhiteSpace(row.Sku) && ctx.SkuToProductId.ContainsKey(row.Sku))
                return (false, null, $"A product with SKU '{row.Sku}' already exists (fill in its Product ID column to update it instead)");

            var slug = Slugify(value);
            if (string.IsNullOrEmpty(slug)) slug = "variant";
            var childSku = !string.IsNullOrWhiteSpace(row.Sku) ? row.Sku : Truncate($"{parent.Sku}-{slug}", 60);
            var suffix = 1;
            while (ctx.SkuToProductId.ContainsKey(childSku))
                childSku = Truncate($"{parent.Sku}-{slug}-{suffix++}", 60);
            // Reserve immediately — see the matching comment in
            // StageProductRowAsync for why this can't wait until the batch
            // actually saves.
            ctx.SkuToProductId[childSku] = 0;

            var parentFlatEn = parent.Flats.FirstOrDefault(f => f.Locale == "en") ?? parent.Flats.FirstOrDefault();
            var parentFlatTe = parent.Flats.FirstOrDefault(f => f.Locale == "te");

            var childUrlKeyBase = $"{parentFlatEn?.UrlKey}-{slug}";
            var childUrlKey = childUrlKeyBase;
            var urlSuffix = 1;
            while (ctx.UsedUrlKeys.Contains(childUrlKey) || await _db.ProductFlats.AnyAsync(f => f.UrlKey == childUrlKey))
                childUrlKey = $"{childUrlKeyBase}-{urlSuffix++}";
            ctx.UsedUrlKeys.Add(childUrlKey);

            child = new Product
            {
                Sku = childSku,
                ParentId = parent.Id,
                Type = "simple",
                AttributeFamilyId = parent.AttributeFamilyId,
                // No Variant Label column anymore — matches the admin
                // "Add Variant" API's own default when no label is given.
                Additional = JsonSerializer.Serialize(new { variant_value = value, variant_label = "Variant", variant_position = 0 }),
                CreatedAt = now,
                UpdatedAt = now
            };

            child.Flats.Add(new ProductFlat
            {
                Sku = childSku,
                Type = "simple",
                Name = $"{parentFlatEn?.Name} - {value}",
                UrlKey = childUrlKey,
                Status = row.Active,
                VisibleIndividually = false,
                Price = row.Price!.Value,
                SpecialPrice = row.SpecialPrice,
                Weight = 1,
                MinQty = row.MinQty,
                MaxQty = row.MaxQty,
                Locale = "en",
                Channel = "default",
                AttributeFamilyId = parent.AttributeFamilyId,
                CreatedAt = now,
                UpdatedAt = now
            });
            if (parentFlatTe != null)
            {
                child.Flats.Add(new ProductFlat
                {
                    Sku = childSku,
                    Type = "simple",
                    Name = $"{parentFlatTe.Name} - {value}",
                    UrlKey = childUrlKey,
                    Status = row.Active,
                    VisibleIndividually = false,
                    Price = row.Price!.Value,
                    SpecialPrice = row.SpecialPrice,
                    Weight = 1,
                    MinQty = row.MinQty,
                    MaxQty = row.MaxQty,
                    Locale = "te",
                    Channel = "default",
                    AttributeFamilyId = parent.AttributeFamilyId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            child.Inventories.Add(new ProductInventory { Qty = row.StockQty, InventorySourceId = ctx.InventorySourceId });
            _db.Products.Add(child);

            return (true, new StagedRow { Row = row, Created = true, Changed = true, Product = child }, null);
        }
        else
        {
            var changed = false;

            if (!string.IsNullOrWhiteSpace(row.Sku) && row.Sku != child!.Sku)
            {
                if (ctx.SkuToProductId.TryGetValue(row.Sku, out var conflictId) && conflictId != child.Id)
                    return (false, null, $"Cannot rename to SKU '{row.Sku}' — already used by product ID {conflictId}");
                child.Sku = row.Sku;
                changed = true;
            }

            // No Variant Label column to read an intended label from — only
            // variant_value is ever updated here, and the existing label
            // (e.g. a real "Choose Weight *" set by the original seeder or
            // the admin API) is always preserved rather than reset to a
            // generic default on every re-import.
            var extras = ProductService.ReadChildVariantExtras(child!);
            if (extras.Value != value)
            {
                child!.Additional = JsonSerializer.Serialize(new { variant_value = value, variant_label = extras.Label, variant_position = extras.Position });
                changed = true;
            }

            foreach (var f in child!.Flats)
            {
                var flatChanged = false;
                flatChanged |= SetIfDifferent(v => f.Sku = v, f.Sku, child.Sku);
                flatChanged |= SetIfDifferent(v => f.Price = v, f.Price, row.Price!.Value);
                flatChanged |= SetIfDifferent(v => f.SpecialPrice = v, f.SpecialPrice, row.SpecialPrice);
                flatChanged |= SetIfDifferent(v => f.Status = v, f.Status, row.Active);
                flatChanged |= SetIfDifferent(v => f.MinQty = v, f.MinQty, row.MinQty);
                flatChanged |= SetIfDifferent(v => f.MaxQty = v, f.MaxQty, row.MaxQty);
                if (flatChanged) { f.UpdatedAt = now; changed = true; }
            }

            var inventory = child.Inventories.FirstOrDefault();
            if (inventory != null)
                changed |= SetIfDifferent(v => inventory.Qty = v, inventory.Qty, row.StockQty);
            else
            {
                child.Inventories.Add(new ProductInventory { Qty = row.StockQty, InventorySourceId = ctx.InventorySourceId });
                changed = true;
            }

            if (changed) child.UpdatedAt = now;

            return (true, new StagedRow { Row = row, Created = false, Changed = changed, Product = child }, null);
        }
    }

    /// <summary>
    /// Assigns <paramref name="incoming"/> via <paramref name="setter"/> and
    /// reports whether it actually differs from <paramref name="current"/> —
    /// used throughout the update path so re-importing a row that hasn't
    /// really changed doesn't count (or write to the DB) as an update.
    /// </summary>
    /// <summary>
    /// Merges vendor_en/vendor_te into <paramref name="existingJson"/>
    /// (product.Additional) instead of replacing it outright — scraped
    /// products carry other keys there too (site, seeder, source_url, …)
    /// that a wholesale `JsonSerializer.Serialize(new { vendor_en, vendor_te })`
    /// would silently discard. Falls back to a fresh object when there's no
    /// existing JSON (new product) or it doesn't parse as an object.
    /// </summary>
    private static string BuildAdditionalJson(string? existingJson, string vendorEn, string vendorTe)
    {
        JsonObject obj;
        try
        {
            obj = (!string.IsNullOrWhiteSpace(existingJson) ? JsonNode.Parse(existingJson) as JsonObject : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }
        obj["vendor_en"] = vendorEn;
        obj["vendor_te"] = vendorTe;
        return obj.ToJsonString();
    }

    private static bool SetIfDifferent<T>(Action<T> setter, T current, T incoming)
    {
        if (EqualityComparer<T>.Default.Equals(current, incoming)) return false;
        setter(incoming);
        return true;
    }

    private static string CleanErrorMessage(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Length > 300 ? message[..300] + "…" : message;
    }

    /// <summary>Walks a category up to its top-level ancestor (the category
    /// whose own parent is the hidden tree root — id 1 in this catalog) and
    /// returns that ancestor's display name, e.g. "Fruits & Vegetables" (a
    /// sub-category) resolves to "Food Store". Bounded by the tree's real
    /// depth (2-3 levels in this catalog), so this is a handful of extra
    /// round trips at most — only run for a brand-new product with a blank
    /// SKU, not on every row.</summary>
    private async Task<string?> GetTopLevelCategoryNameAsync(Category category)
    {
        var current = category;
        while (current.ParentId.HasValue)
        {
            var parent = await _db.Categories.Include(c => c.Translations)
                .FirstOrDefaultAsync(c => c.Id == current.ParentId!.Value);
            if (parent == null || !parent.ParentId.HasValue)
                break; // parent is the hidden root (or missing) — current is top-level
            current = parent;
        }
        return current.Translations.FirstOrDefault(t => t.Locale == "en")?.Name
            ?? current.Translations.FirstOrDefault()?.Name;
    }

    /// <summary>Next SKU in an existing vertical's numbering (e.g.
    /// "FOODSTORE004668" after the real catalog's last "FOODSTORE004667") —
    /// continues the real sequence instead of restarting at 1, which would
    /// otherwise sit oddly next to ~2000 legitimately-numbered SKUs in the
    /// same prefix. Falls back to starting at 1 for a prefix with no
    /// existing products (e.g. a vertical nobody's used yet).</summary>
    private async Task<string> NextSkuInVerticalAsync(string prefix, ImportContext ctx)
    {
        var candidates = await _db.Products
            .Where(p => p.Sku.StartsWith(prefix))
            .OrderByDescending(p => p.Sku)
            .Select(p => p.Sku)
            .Take(50)
            .ToListAsync();

        var maxSeq = 0;
        foreach (var candidateSku in candidates)
        {
            // Variant SKUs share the same prefix (e.g. "FOODSTORE004667-1kg")
            // but sort above their own plain parent SKU as strings — skip
            // anything that isn't a clean "<prefix><digits>" match.
            var rest = candidateSku.Substring(prefix.Length);
            if (int.TryParse(rest, out var n) && n > maxSeq)
                maxSeq = n;
        }

        var seq = maxSeq + 1;
        string sku;
        do { sku = $"{prefix}{seq:D6}"; seq++; }
        while (ctx.SkuToProductId.ContainsKey(sku));
        return sku;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    private static string Slugify(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "-");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"-+", "-");
        return s.Trim('-');
    }
}
