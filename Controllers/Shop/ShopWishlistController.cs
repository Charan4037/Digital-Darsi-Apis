using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;
using DOSApi.Helpers;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/wishlists")]
[Tags("Wishlist")]
[Authorize]
public class ShopWishlistController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;
    private readonly string _baseUrl;

    public ShopWishlistController(DOSDbContext db, ProductService productService, IConfiguration config)
    {
        _db = db;
        _productService = productService;
        _baseUrl = config["App:BaseUrl"] ?? "http://localhost:8000";
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customer_id")?.Value ?? "0");

    // â”€â”€ Map product to DOS WishlistResource shape â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
            ["vendor_name"] = _productService.GetProductVendor(p),
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

    private object MapWishlistItem(Wishlist w)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = w.Id,
            ["product"] = w.Product != null ? MapProduct(w.Product) : null,
            ["options"] = Array.Empty<object>()
        };
    }

    /// <summary>List wishlist items</summary>
    [HttpGet]
    public async Task<IActionResult> GetWishlistItems()
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        await _productService.EnsureAttrIdsAsync();

        var items = await _db.Wishlists
            .Include(w => w.Product).ThenInclude(p => p!.Images)
            .Include(w => w.Product).ThenInclude(p => p!.AttributeValues)
            .Include(w => w.Product).ThenInclude(p => p!.Flats)
            .Include(w => w.Product).ThenInclude(p => p!.Reviews.Where(r => r.Status == "approved"))
            .Include(w => w.Product).ThenInclude(p => p!.Inventories)
            .Where(w => w.CustomerId == customerId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        var data = items.Select(MapWishlistItem);

        return Ok(data);
    }

    /// <summary>Get a single wishlist item</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetWishlistItem(int id)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        await _productService.EnsureAttrIdsAsync();

        var w = await _db.Wishlists
            .Include(w => w.Product).ThenInclude(p => p!.Images)
            .Include(w => w.Product).ThenInclude(p => p!.AttributeValues)
            .Include(w => w.Product).ThenInclude(p => p!.Flats)
            .Include(w => w.Product).ThenInclude(p => p!.Reviews.Where(r => r.Status == "approved"))
            .Include(w => w.Product).ThenInclude(p => p!.Inventories)
            .FirstOrDefaultAsync(w => w.Id == id && w.CustomerId == customerId);

        if (w == null) return NotFound(new { message = "Wishlist item not found." });

        return Ok(MapWishlistItem(w));
    }

    public record AddToWishlistRequest(int ProductId);

    /// <summary>Add product to wishlist</summary>
    [HttpPost]
    public async Task<IActionResult> AddToWishlist([FromBody] AddToWishlistRequest req)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var product = await _db.Products.FindAsync(req.ProductId);
        if (product == null) return NotFound(new { message = "Product not found." });

        var exists = await _db.Wishlists.AnyAsync(w => w.CustomerId == customerId && w.ProductId == req.ProductId);
        if (exists) return Ok(new { message = "Product is already in wishlist." });

        var now = DateTime.UtcNow;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO wishlist_items (channel_id, product_id, customer_id, created_at, updated_at) VALUES (1, {req.ProductId}, {customerId}, {now}, {now})");

        return Ok(new { message = "Product added to wishlist successfully." });
    }

    /// <summary>Remove from wishlist</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> RemoveFromWishlist(int id)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var item = await _db.Wishlists.FirstOrDefaultAsync(w => w.Id == id && w.CustomerId == customerId);
        if (item == null) return NotFound(new { message = "Wishlist item not found." });

        _db.Wishlists.Remove(item);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Item removed from wishlist successfully." });
    }
}
