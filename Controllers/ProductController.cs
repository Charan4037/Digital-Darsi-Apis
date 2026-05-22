using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/v1/products")]
[Tags("Products")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class ProductController : ControllerBase
{
    private readonly ProductService _productService;
    private readonly BagistoDbContext _db;
    private readonly string _locale;

    public ProductController(ProductService productService, BagistoDbContext db, LocaleContext localeCtx)
    {
        _productService = productService;
        _db = db;
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

        var data = items.Select(p => new
        {
            p.Id,
            p.Sku,
            p.Type,
            Name = _productService.GetProductName(p),
            UrlKey = _productService.GetProductUrlKey(p),
            Price = _productService.GetProductPrice(p),
            SpecialPrice = _productService.GetProductSpecialPrice(p),
            FormattedPrice = $"₹{_productService.GetEffectivePrice(p):N2}",
            ShortDescription = _productService.GetProductShortDescription(p),
            BaseImage = _productService.GetBaseImageUrl(p),
            Images = p.Images.Select(i => _productService.GetImagePublicPath(i)),
            InStock = _productService.IsSaleable(p),
            HasVariants = p.Type == "configurable",
            ReviewsCount = p.Reviews.Count,
            AverageRating = p.Reviews.Any() ? p.Reviews.Average(r => r.Rating) : 0,
            VendorName = _productService.GetProductVendor(p),
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
                }).ToList(),
            p.CreatedAt
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
        var price = _productService.GetProductPrice(p);
        var specialPrice = _productService.GetProductSpecialPrice(p);
        var description = _productService.GetProductDescription(p);
        var shortDesc = _productService.GetProductShortDescription(p);

        if (name == p.Sku || price == 0 || description == null)
        {
            var flat = p.Flats.FirstOrDefault(f => f.Locale == _locale)
                       ?? p.Flats.FirstOrDefault();
            if (flat != null)
            {
                if (name == p.Sku && !string.IsNullOrEmpty(flat.Name)) name = flat.Name!;
                if (price == 0 && flat.Price.HasValue) price = flat.Price.Value;
                if (!specialPrice.HasValue && flat.SpecialPrice.HasValue) specialPrice = flat.SpecialPrice;
                resolvedUrlKey ??= flat.UrlKey;
                description ??= flat.Description;
                shortDesc ??= flat.ShortDescription;
            }
        }

        var effectivePrice = (specialPrice.HasValue && specialPrice > 0)
            ? specialPrice.Value
            : price;

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
                FormattedPrice = $"₹{effectivePrice:N2}",
                Description = description,
                ShortDescription = shortDesc,
                BaseImage = _productService.GetBaseImageUrl(p),
                Images = p.Images.Select(i => new { i.Id, Url = _productService.GetImagePublicPath(i), i.Position }),
                InStock = _productService.IsSaleable(p),
                ReviewsCount = p.Reviews.Count,
                AverageRating = p.Reviews.Any() ? p.Reviews.Average(r => r.Rating) : 0,
                VendorName = _productService.GetProductVendor(p),
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
                    }).ToList(),
                Reviews = p.Reviews.Select(r => new { r.Id, r.Title, r.Comment, r.Rating, r.Name, r.Status, r.CreatedAt }),
                Variants = p.Children.Select(c => new
                {
                    c.Id,
                    c.Sku,
                    Name = _productService.GetProductName(c),
                    Price = _productService.GetProductPrice(c),
                    SpecialPrice = _productService.GetProductSpecialPrice(c),
                    InStock = _productService.IsSaleable(c),
                    Images = c.Images.Select(i => _productService.GetImagePublicPath(i))
                }),
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
                    return new
                    {
                        rp.Id,
                        Name = rpName,
                        Price = rpPrice,
                        BaseImage = _productService.GetBaseImageUrl(rp)
                    };
                }),
                p.CreatedAt
            }
        });
    }

    /// <summary>Get related products for a product</summary>
    [HttpGet("{id:int}/related")]
    public async Task<IActionResult> GetRelatedProducts(int id)
    {
        var product = await _db.Products
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
