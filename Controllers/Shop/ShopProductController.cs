using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Data;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/products")]
[Tags("Product")]
[AllowAnonymous]
public class ShopProductController : ControllerBase
{
    private readonly ProductService _productService;
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopProductController(ProductService productService, BagistoDbContext db, IConfiguration config)
    {
        _productService = productService;
        _db = db;
        _baseUrl = config["App:BaseUrl"] ?? "http://localhost:8000";
    }

    // -- Formatting helpers --

    private static string FormatPrice(decimal price) => price.ToString("N2");

    private static string FormatCurrency(decimal price) => $"${price:N2}";

    private static string FormatPriceRaw(decimal price) => $"{price:F4}";

    private static string BuildPriceHtml(decimal regularPrice, decimal finalPrice)
    {
        if (finalPrice < regularPrice)
        {
            return $"<div class=\"price-container\">"
                 + $"<p class=\"regular-price\"><span class=\"line-through\">{FormatCurrency(regularPrice)}</span></p>"
                 + $"<p class=\"sale-price\">{FormatCurrency(finalPrice)}</p>"
                 + $"</div>";
        }

        return $"<p class=\"final-price\">{FormatCurrency(finalPrice)}</p>";
    }

    // -- Map a product to the Bagisto resource shape --

    private object MapProductToResource(Models.Catalog.Product p, bool includeDetail = false)
    {
        var name = _productService.GetProductName(p);
        var description = _productService.GetProductDescription(p);
        var shortDescription = _productService.GetProductShortDescription(p);
        var urlKey = _productService.GetProductUrlKey(p);

        var regularPrice = _productService.GetProductPrice(p);
        var specialPrice = _productService.GetProductSpecialPrice(p);
        var effectivePrice = _productService.GetEffectivePrice(p);

        var onSale = specialPrice.HasValue && specialPrice.Value > 0 && specialPrice.Value < regularPrice;

        var isSaleable = _productService.IsSaleable(p);

        // Base image (first image)
        var firstImage = p.Images.OrderBy(i => i.Position).FirstOrDefault();
        var baseImage = ImageHelper.ProductImage(firstImage?.Path, _baseUrl, p.Id);

        // All images
        var images = p.Images.OrderBy(i => i.Position)
            .Select(i => ImageHelper.ProductImage(i.Path, _baseUrl, p.Id))
            .ToList();

        // If no images, provide at least the placeholder as base_image
        if (!images.Any())
        {
            images.Add(baseImage);
        }

        // Ratings & reviews
        var reviewCount = p.Reviews.Count;
        var averageRating = reviewCount > 0
            ? p.Reviews.Average(r => r.Rating)
            : 0.0;

        // is_new and is_featured: default false for now
        var isNew = 0;
        var isFeatured = 0;

        // Digital Darsi seeder writes vendor + variation metadata to Product.Additional.
        var vendorName = _productService.GetProductVendor(p);
        var variations = _productService.GetProductVariations(p);

        if (!includeDetail)
        {
            return new
            {
                p.Id,
                p.Sku,
                Name = name,
                Description = description,
                UrlKey = urlKey,
                BaseImage = baseImage,
                Images = images,
                IsNew = isNew,
                IsFeatured = isFeatured,
                OnSale = onSale ? 1 : 0,
                IsSaleable = isSaleable ? 1 : 0,
                IsWishlist = 0,
                VendorName = vendorName,
                Variations = variations.Select(v => new
                {
                    Label = v.Label,
                    Value = v.Value,
                    ProductId = v.ProductId,
                    Price = v.Price,
                    SpecialPrice = v.SpecialPrice,
                    FormattedPrice = v.FormattedPrice,
                    InStock = v.InStock,
                }).ToList(),
                MinPrice = FormatCurrency(effectivePrice),
                Prices = new
                {
                    Regular = new
                    {
                        Price = FormatPriceRaw(regularPrice),
                        FormattedPrice = FormatCurrency(regularPrice)
                    },
                    Final = new
                    {
                        Price = FormatPriceRaw(effectivePrice),
                        FormattedPrice = FormatCurrency(effectivePrice)
                    }
                },
                PriceHtml = BuildPriceHtml(regularPrice, effectivePrice),
                Ratings = new
                {
                    Average = averageRating.ToString("F1"),
                    Total = reviewCount
                },
                Reviews = new
                {
                    Total = reviewCount
                }
            };
        }

        return new
        {
            p.Id,
            p.Sku,
            Name = name,
            Description = description,
            ShortDescription = shortDescription,
            UrlKey = urlKey,
            Type = p.Type,
            BaseImage = baseImage,
            Images = images,
            IsNew = isNew,
            IsFeatured = isFeatured,
            OnSale = onSale ? 1 : 0,
            IsSaleable = isSaleable ? 1 : 0,
            IsWishlist = 0,
            VendorName = vendorName,
            Variations = variations.Select(v => new { Label = v.Label, Value = v.Value }).ToList(),
            MinPrice = FormatCurrency(effectivePrice),
            Prices = new
            {
                Regular = new
                {
                    Price = FormatPriceRaw(regularPrice),
                    FormattedPrice = FormatCurrency(regularPrice)
                },
                Final = new
                {
                    Price = FormatPriceRaw(effectivePrice),
                    FormattedPrice = FormatCurrency(effectivePrice)
                }
            },
            PriceHtml = BuildPriceHtml(regularPrice, effectivePrice),
            Ratings = new
            {
                Average = averageRating.ToString("F1"),
                Total = reviewCount
            },
            Reviews = new
            {
                Total = reviewCount
            },
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            ReviewsList = p.Reviews.Select(r => new
            {
                r.Id,
                r.Title,
                r.Comment,
                r.Rating,
                r.Name,
                r.Status,
                r.CreatedAt
            }).ToList(),
            Variants = p.Children.Select(c => new
            {
                c.Id,
                c.Sku,
                Name = _productService.GetProductName(c),
                Price = _productService.GetProductPrice(c),
                SpecialPrice = _productService.GetProductSpecialPrice(c),
                IsSaleable = _productService.IsSaleable(c) ? 1 : 0,
                Images = c.Images.OrderBy(i => i.Position)
                    .Select(i => ImageHelper.ProductImage(i.Path, _baseUrl, c.Id))
                    .ToList()
            }).ToList(),
            SuperAttributes = p.SuperAttributes.Select(sa => new
            {
                sa.Id,
                sa.Code,
                AdminName = sa.AdminName,
                Options = sa.Options.Select(o => new
                {
                    o.Id,
                    Label = o.Translations.FirstOrDefault()?.Label ?? o.AdminName
                }).ToList()
            }).ToList(),
            RelatedProducts = p.RelatedProducts.Select(rp =>
                MapProductToResource(rp, false)).ToList()
        };
    }

    // -- Endpoints --

    /// <summary>List all products with filtering and sorting</summary>
    [HttpGet]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? query,
        [FromQuery] string? filter,
        [FromQuery] string? sortKey,
        [FromQuery] bool reverse = false)
    {
        var (items, _) = await _productService.QueryProductsAsync(filter, sortKey, reverse, query, 0, int.MaxValue);

        var data = items.Select(p => MapProductToResource(p, false)).ToList();

        return Ok(data);
    }

    /// <summary>Get a single product by URL key</summary>
    [HttpGet("{urlKey}")]
    public async Task<IActionResult> GetProduct(string urlKey)
    {
        var p = await _productService.GetProductByUrlKeyAsync(urlKey);
        if (p == null) return NotFound(new { message = "Product not found." });

        return Ok(MapProductToResource(p, true));
    }
}
