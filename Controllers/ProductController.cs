using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Helpers;
using DOSApi.Services;

namespace DOSApi.Controllers;

[ApiController]
[Route("api/v1/products")]
[Tags("Products")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class ProductController : ControllerBase
{
    private readonly ProductService _productService;
    private readonly DOSDbContext _db;
    private readonly PreorderService _preorderService;
    private readonly string _locale;

    public ProductController(ProductService productService, DOSDbContext db, PreorderService preorderService, LocaleContext localeCtx)
    {
        _productService = productService;
        _db = db;
        _preorderService = preorderService;
        _locale = localeCtx.Locale;
    }

    /// <summary>List all products with filtering, sorting and pagination</summary>
    [HttpGet]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? query,
        [FromQuery] string? filter,
        [FromQuery] string? sortKey,
        [FromQuery] bool reverse = false,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 10)
    {
        var offset = (page - 1) * limit;
        var (items, totalCount) = await _productService.QueryProductsAsync(filter, sortKey, reverse, query, offset, limit);

        var preorderProductIds = await _preorderService.ResolveListPreorderStatusAsync(items.Select(p => p.Id).ToList(), null);

        var data = items.Select(p =>
        {
            var pricingProduct = _productService.GetPricingProduct(p);
            var price = _productService.GetProductPrice(pricingProduct);
            var specialPrice = _productService.GetProductSpecialPrice(pricingProduct);
            var effectivePrice = (specialPrice.HasValue && specialPrice > 0) ? specialPrice.Value : price;
            return new
            {
                p.Id,
                p.Sku,
                p.Type,
                Name = _productService.GetProductName(p),
                UrlKey = _productService.GetProductUrlKey(p),
                Price = price,
                SpecialPrice = specialPrice,
                FormattedPrice = PriceFormatter.Format(effectivePrice),
                ShortDescription = _productService.GetProductShortDescription(p),
                BaseImage = _productService.GetBaseImageUrl(p),
                Images = p.Images.OrderBy(i => i.Position).Take(5).Select(i => _productService.GetImagePublicPath(i)),
                InStock = _productService.IsSaleable(pricingProduct),
                HasVariants = p.Type == "configurable",
                ReviewsCount = p.Reviews.Count(r => r.Status == "approved"),
                AverageRating = p.Reviews.Any(r => r.Status == "approved") ? p.Reviews.Where(r => r.Status == "approved").Average(r => r.Rating) : 0,
                VendorName = _productService.GetProductVendor(p),
                MinQty = _productService.GetFlat(p)?.MinQty ?? 1,
                MaxQty = _productService.GetFlat(p)?.MaxQty,
                Variations = _productService.GetProductVariations(p)
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
                p.CreatedAt,
                IsPreorder = preorderProductIds.Contains(p.Id),
            };
        });

        return Ok(new
        {
            data,
            meta = new { total = totalCount, currentPage = page, perPage = limit, lastPage = (int)Math.Ceiling((double)totalCount / limit) }
        });
    }

    /// <summary>Get a single product by URL key</summary>
    [HttpGet("{urlKey}")]
    public async Task<IActionResult> GetProduct(string urlKey)
    {
        var p = await _productService.GetProductByUrlKeyAsync(urlKey);
        if (p == null) return NotFound(new { message = "Product not found." });

        // Try EAV (product_attribute_values) first; fall back to product_flat
        // when the attribute row is missing. Mirrors the category-products
        // endpoint so both list + detail payloads agree on name/price.
        var name = _productService.GetProductName(p);
        var resolvedUrlKey = _productService.GetProductUrlKey(p);
        // For product detail page: use parent product prices, not variant prices
        var price = _productService.GetProductPrice(p);
        var specialPrice = _productService.GetProductSpecialPrice(p);
        var description = _productService.GetProductDescription(p);
        var shortDesc = _productService.GetProductShortDescription(p);

        if (name == p.Sku || description == null)
        {
            var flat = p.Flats.FirstOrDefault(f => f.Locale == _locale)
                       ?? p.Flats.FirstOrDefault();
            if (flat != null)
            {
                if (name == p.Sku && !string.IsNullOrEmpty(flat.Name)) name = flat.Name!;
                resolvedUrlKey ??= flat.UrlKey;
                description ??= flat.Description;
                shortDesc ??= flat.ShortDescription;
            }
        }
        if (price == 0 || !specialPrice.HasValue)
        {
            var pricingFlat = p.Flats.FirstOrDefault(f => f.Locale == _locale)
                               ?? p.Flats.FirstOrDefault();
            if (pricingFlat != null)
            {
                if (price == 0 && pricingFlat.Price.HasValue) price = pricingFlat.Price.Value;
                if (!specialPrice.HasValue && pricingFlat.SpecialPrice.HasValue) specialPrice = pricingFlat.SpecialPrice;
            }
        }

        var effectivePrice = (specialPrice.HasValue && specialPrice > 0)
            ? specialPrice.Value
            : price;

        // Vendors have no FK from products — resolve the free-text vendor
        // name (from Additional JSON) against the vendors table by name
        // (unique) so the client has a stable id to open a vendor store
        // page with, instead of just a display string.
        var vendorName = _productService.GetProductVendor(p);
        var vendorId = string.IsNullOrWhiteSpace(vendorName)
            ? (int?)null
            : (await _db.Vendors.FirstOrDefaultAsync(v => v.Name == vendorName || v.NameTe == vendorName))?.Id;

        var preorderInfo = await _preorderService.ResolveAsync(p.Id, null);

        return Ok(new
        {
            data = new
            {
                p.Id,
                p.Sku,
                p.Type,
                Name = name,
                UrlKey = resolvedUrlKey,
                Price = price,
                SpecialPrice = specialPrice,
                FormattedPrice = PriceFormatter.Format(effectivePrice),
                MinQty = _productService.GetFlat(p)?.MinQty ?? 1,
                MaxQty = _productService.GetFlat(p)?.MaxQty,
                Description = description,
                ShortDescription = shortDesc,
                BaseImage = _productService.GetBaseImageUrl(p),
                Images = p.Images.Select(i => new { i.Id, Url = _productService.GetImagePublicPath(i), i.Position }),
                InStock = _productService.IsSaleable(p),
                ReviewsCount = p.Reviews.Count(r => r.Status == "approved"),
                AverageRating = p.Reviews.Any(r => r.Status == "approved") ? p.Reviews.Where(r => r.Status == "approved").Average(r => r.Rating) : 0,
                VendorName = vendorName,
                VendorId = vendorId,
                Specs = _productService.GetProductSpecs(p)
                    .Select(s => new { name = s.Name, value = s.Value })
                    .ToList(),
                Variations = _productService.GetProductVariations(p)
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
                Reviews = p.Reviews.Where(r => r.Status == "approved").OrderByDescending(r => r.CreatedAt).Select(r => new { r.Id, r.Title, r.Comment, r.Rating, r.Name, r.CreatedAt }),
                SuperAttributes = p.SuperAttributes.Select(sa => new
                {
                    sa.Id,
                    sa.Code,
                    sa.AdminName,
                    Options = sa.Options.Select(o => new
                    {
                        o.Id,
                        Label = o.Translations.FirstOrDefault()?.Label ?? o.AdminName
                    })
                }),
                RelatedProducts = p.RelatedProducts.Select(rp =>
                {
                    var rpName = _productService.GetProductName(rp);
                    var rpPrice = _productService.GetProductPrice(rp);
                    if (rpName == rp.Sku || rpPrice == 0)
                    {
                        var flat = rp.Flats.FirstOrDefault(f => f.Locale == _locale)
                                   ?? rp.Flats.FirstOrDefault();
                        if (flat != null)
                        {
                            if (rpName == rp.Sku && !string.IsNullOrEmpty(flat.Name)) rpName = flat.Name!;
                            if (rpPrice == 0 && flat.Price.HasValue) rpPrice = flat.Price.Value;
                        }
                    }
                    var rpApproved = rp.Reviews?.Where(r => r.Status == "approved").ToList();
                    return new
                    {
                        rp.Id,
                        Name = rpName,
                        Price = rpPrice,
                        BaseImage = _productService.GetBaseImageUrl(rp),
                        ReviewsCount = rpApproved?.Count ?? 0,
                        AverageRating = (rpApproved?.Count ?? 0) > 0
                            ? rpApproved!.Average(r => r.Rating)
                            : 0,
                    };
                }),
                p.CreatedAt,
                IsPreorder = preorderInfo != null,
                PreorderWindowDays = preorderInfo?.Rule.WindowDays ?? PreorderService.DefaultWindowDays,
                PreorderMinLeadHours = preorderInfo?.Rule.MinLeadHours ?? PreorderService.DefaultMinLeadHours,
                PreorderSlotsCount = preorderInfo?.Slots.Count(s => s.IsActive) ?? 0,
            }
        });
    }

    /// <summary>Get related products for a product</summary>
    [HttpGet("{id:int}/related")]
    public async Task<IActionResult> GetRelatedProducts(int id)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.AttributeValues)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Images)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Reviews)
            .Include(p => p.RelatedProducts).ThenInclude(rp => rp.Inventories)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null) return NotFound(new { message = "Product not found." });

        var data = product.RelatedProducts.Select(p => new
        {
            p.Id,
            Name = _productService.GetProductName(p),
            Price = _productService.GetProductPrice(p),
            SpecialPrice = _productService.GetProductSpecialPrice(p),
            BaseImage = _productService.GetBaseImageUrl(p),
            InStock = _productService.IsSaleable(p)
        });

        return Ok(new { data });
    }

    /// <summary>Get up-sell products for a product</summary>
    [HttpGet("{id:int}/up-sell")]
    public async Task<IActionResult> GetUpSellProducts(int id)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Include(p => p.UpSells).ThenInclude(rp => rp.AttributeValues)
            .Include(p => p.UpSells).ThenInclude(rp => rp.Images)
            .Include(p => p.UpSells).ThenInclude(rp => rp.Inventories)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null) return NotFound(new { message = "Product not found." });

        var data = product.UpSells.Select(p => new
        {
            p.Id,
            Name = _productService.GetProductName(p),
            Price = _productService.GetProductPrice(p),
            SpecialPrice = _productService.GetProductSpecialPrice(p),
            BaseImage = _productService.GetBaseImageUrl(p),
            InStock = _productService.IsSaleable(p)
        });

        return Ok(new { data });
    }
}
