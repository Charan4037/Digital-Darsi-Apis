using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Helpers;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// Public, consumer-facing vendor storefront — profile, product listing and
/// categories for the vendor a product belongs to. Distinct from the Admin
/// vendor endpoints (Controllers/Admin/AdminVendorsController.cs), which
/// also expose order/revenue data that shouldn't be public.
///
/// There's no FK from products to vendors — a product's vendor is a
/// free-text name embedded in its `additional` JSON (see
/// ProductService.ExtractVendorName / VendorAggregationService), matched
/// against the `vendors` table by name (unique). So "this vendor's
/// products" is always resolved as an in-memory id set, the same pattern
/// AdminVendorsController and VendorAggregationService already use.
/// </summary>
[ApiController]
[Route("api/shop/vendors")]
[Tags("Vendor")]
[AllowAnonymous]
public class ShopVendorController : ControllerBase
{
    private readonly ProductService _productService;
    private readonly VendorAggregationService _aggregation;
    private readonly DOSDbContext _db;
    private readonly string _locale;

    public ShopVendorController(
        ProductService productService,
        VendorAggregationService aggregation,
        DOSDbContext db,
        LocaleContext localeCtx)
    {
        _productService = productService;
        _aggregation = aggregation;
        _db = db;
        _locale = localeCtx.Locale;
    }

    private async Task<HashSet<int>> GetVendorProductIdsAsync(string vendorName)
    {
        var map = await _aggregation.BuildProductVendorMapAsync();
        return map
            .Where(kv => string.Equals(kv.Value, vendorName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();
    }

    /// <summary>Public vendor profile: name + how many products they sell.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetVendor(int id)
    {
        var vendor = await _db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.Active);
        if (vendor == null) return NotFound(new { message = "Vendor not found." });

        var vendorProductIds = await GetVendorProductIdsAsync(vendor.Name);
        var productCount = await _db.Products
            .CountAsync(p => p.ParentId == null && vendorProductIds.Contains(p.Id) && p.Flats.Any(f => f.Status == true));

        return Ok(new
        {
            data = new
            {
                vendor.Id,
                vendor.Name,
                vendor.NameTe,
                ProductCount = productCount,
            }
        });
    }

    /// <summary>This vendor's products, in the same card shape every other
    /// product listing in the app uses (Home, Category browse).</summary>
    [HttpGet("{id:int}/products")]
    public async Task<IActionResult> GetVendorProducts(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] int? categoryId = null)
    {
        var vendor = await _db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.Active);
        if (vendor == null) return NotFound(new { message = "Vendor not found." });

        await _productService.EnsureAttrIdsAsync();
        var vendorProductIds = await GetVendorProductIdsAsync(vendor.Name);

        var baseQ = _db.Products
            .Where(p => p.ParentId == null
                && vendorProductIds.Contains(p.Id)
                && p.Flats.Any(f => f.Status == true));

        if (categoryId.HasValue)
            baseQ = baseQ.Where(p => p.Categories.Any(c => c.Id == categoryId.Value));

        var totalCount = await baseQ.CountAsync();
        var offset = (Math.Max(page, 1) - 1) * limit;

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

    /// <summary>Distinct categories this vendor's products fall under —
    /// powers the vendor store page's "Categories" tab.</summary>
    [HttpGet("{id:int}/categories")]
    public async Task<IActionResult> GetVendorCategories(int id)
    {
        var vendor = await _db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.Active);
        if (vendor == null) return NotFound(new { message = "Vendor not found." });

        var vendorProductIds = await GetVendorProductIdsAsync(vendor.Name);

        var categories = await _db.Categories
            .Include(c => c.Translations)
            .Where(c => c.Products.Any(p => vendorProductIds.Contains(p.Id)))
            .AsNoTracking()
            .ToListAsync();

        var data = categories
            .Select(c => new
            {
                c.Id,
                Name = (c.Translations.FirstOrDefault(t => t.Locale == _locale) ?? c.Translations.FirstOrDefault())?.Name,
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Name)
            .ToList();

        return Ok(new { data });
    }

    /// <summary>Projects a product into the storefront list-card shape —
    /// mirrors CategoryController.BuildProductCard so a vendor's product
    /// grid renders identically to Home/Category listings. Kept as its own
    /// copy rather than a shared helper to avoid touching that already-live
    /// code path for this addition.</summary>
    private object BuildProductCard(Product p)
    {
        var name = _productService.GetProductName(p);
        var pricingProduct = _productService.GetPricingProduct(p);
        var price = _productService.GetProductPrice(pricingProduct);
        var specialPrice = _productService.GetProductSpecialPrice(pricingProduct);
        var urlKey = _productService.GetProductUrlKey(p);
        var shortDesc = _productService.GetProductShortDescription(p);

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

        // Real configurable children aren't the only source of variants —
        // GetProductVariations also derives them from scraped metadata in
        // Additional for "simple"-type products (see its path 2). Deriving
        // HasVariants from that same list (rather than just p.Type ==
        // "configurable") keeps the card's variant pills in sync with what
        // the product-details page actually renders for every product.
        var variations = _productService.GetProductVariations(p);

        return new
        {
            p.Id,
            p.Sku,
            Name = name,
            Description = shortDesc,
            Price = price,
            SpecialPrice = specialPrice,
            FormattedPrice = PriceFormatter.Format(effectivePrice),
            UrlKey = urlKey,
            BaseImage = _productService.GetBaseImageUrl(p)
                        ?? p.Images.FirstOrDefault()?.Path,
            Images = p.Images.Select(i =>
                _productService.GetImagePublicPath(i) ?? i.Path),
            InStock = _productService.IsSaleable(pricingProduct),
            HasVariants = variations.Count > 0,
            AverageRating = avgRating,
            ReviewsCount = reviewCount,
            MinQty = _productService.GetFlat(p)?.MinQty ?? 1,
            MaxQty = _productService.GetFlat(p)?.MaxQty,
            Variations = variations
                .Select(v => new
                {
                    label = v.Label,
                    value = v.Value,
                    productId = v.ProductId,
                    price = v.Price,
                    specialPrice = v.SpecialPrice,
                    formattedPrice = v.FormattedPrice,
                    inStock = v.InStock,
                    minQty = v.MinQty,
                    maxQty = v.MaxQty,
                }).ToList(),
        };
    }
}
