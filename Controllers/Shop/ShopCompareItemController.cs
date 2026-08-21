using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;
using DOSApi.Helpers;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/compare_items")]
[Tags("CompareItem")]
[Authorize]
public class ShopCompareItemController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;
    private readonly string _baseUrl;

    public ShopCompareItemController(DOSDbContext db, ProductService productService, IConfiguration config)
    {
        _db = db;
        _productService = productService;
        _baseUrl = config["App:BaseUrl"] ?? "http://localhost:8000";
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customer_id")?.Value ?? "0");

    // â”€â”€ Map product to DOS CompareItemResource shape â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private Dictionary<string, object?> MapProduct(Models.Catalog.Product p)
    {
        var firstImage = p.Images.OrderBy(i => i.Position).FirstOrDefault();
        var regularPrice = _productService.GetProductPrice(p);
        var specialPrice = _productService.GetProductSpecialPrice(p);
        var effectivePrice = _productService.GetEffectivePrice(p);
        var onSale = specialPrice.HasValue && specialPrice > 0 && specialPrice < regularPrice;

        var reviewCount = p.Reviews.Count;
        var averageRating = reviewCount > 0 ? p.Reviews.Average(r => r.Rating) : 0.0;

        var images = p.Images.OrderBy(i => i.Position)
            .Select(i => ImageHelper.ProductImage(i.Path, _baseUrl, p.Id))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["id"] = p.Id,
            ["sku"] = p.Sku,
            ["name"] = _productService.GetProductName(p),
            ["description"] = _productService.GetProductDescription(p),
            ["url_key"] = _productService.GetProductUrlKey(p),
            ["base_image"] = ImageHelper.ProductImage(firstImage?.Path, _baseUrl, p.Id),
            ["images"] = images,
            ["is_new"] = 0,
            ["is_featured"] = 0,
            ["on_sale"] = onSale ? 1 : 0,
            ["is_saleable"] = _productService.IsSaleable(p) ? 1 : 0,
            ["is_wishlist"] = 0,
            ["min_price"] = PriceFormatter.Format(effectivePrice),
            ["prices"] = new
            {
                regular = new
                {
                    price = regularPrice.ToString("F4"),
                    formatted_price = PriceFormatter.Format(regularPrice)
                },
                final = new
                {
                    price = effectivePrice.ToString("F4"),
                    formatted_price = PriceFormatter.Format(effectivePrice)
                }
            },
            ["price_html"] = $"<p class=\"final-price font-semibold\">{PriceFormatter.Format(effectivePrice)}</p>",
            ["ratings"] = new
            {
                average = averageRating.ToString("F1"),
                total = reviewCount
            },
            ["reviews"] = new
            {
                total = reviewCount
            }
        };
    }

    /// <summary>List compare items</summary>
    [HttpGet]
    public async Task<IActionResult> GetCompareItems()
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var items = await _db.CompareItems
            .Include(ci => ci.Product).ThenInclude(p => p!.Images)
            .Include(ci => ci.Product).ThenInclude(p => p!.AttributeValues)
            .Include(ci => ci.Product).ThenInclude(p => p!.Reviews.Where(r => r.Status == "approved"))
            .Include(ci => ci.Product).ThenInclude(p => p!.Inventories)
            .Where(ci => ci.CustomerId == customerId)
            .OrderByDescending(ci => ci.CreatedAt)
            .ToListAsync();

        var data = items.Select(ci => ci.Product != null ? MapProduct(ci.Product) : (object)new { });

        return Ok(data);
    }

    /// <summary>Get a single compare item</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCompareItem(int id)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var ci = await _db.CompareItems
            .Include(ci => ci.Product).ThenInclude(p => p!.Images)
            .Include(ci => ci.Product).ThenInclude(p => p!.AttributeValues)
            .Include(ci => ci.Product).ThenInclude(p => p!.Reviews.Where(r => r.Status == "approved"))
            .Include(ci => ci.Product).ThenInclude(p => p!.Inventories)
            .FirstOrDefaultAsync(ci => ci.Id == id && ci.CustomerId == customerId);

        if (ci == null) return NotFound(new { message = "Compare item not found." });

        var data = ci.Product != null ? MapProduct(ci.Product) : new Dictionary<string, object?>();

        return Ok(data);
    }

    public record AddToCompareRequest(int ProductId);

    /// <summary>Add product to compare</summary>
    [HttpPost]
    public async Task<IActionResult> AddToCompare([FromBody] AddToCompareRequest req)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var product = await _db.Products.FindAsync(req.ProductId);
        if (product == null) return NotFound(new { message = "Product not found." });

        var exists = await _db.CompareItems.AnyAsync(ci => ci.CustomerId == customerId && ci.ProductId == req.ProductId);
        if (exists) return Ok(new { message = "Product is already in compare list." });

        var compareItem = new CompareItem
        {
            CustomerId = customerId,
            ProductId = req.ProductId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.CompareItems.Add(compareItem);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Product added to compare list successfully." });
    }

    /// <summary>Remove from compare</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> RemoveFromCompare(int id)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var item = await _db.CompareItems.FirstOrDefaultAsync(ci => ci.Id == id && ci.CustomerId == customerId);
        if (item == null) return NotFound(new { message = "Compare item not found." });

        _db.CompareItems.Remove(item);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Item removed from compare list successfully." });
    }
}
