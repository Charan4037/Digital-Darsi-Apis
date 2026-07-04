using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-reviews")]
[Tags("CustomerReview")]
[Authorize]
public class ShopCustomerReviewController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    public ShopCustomerReviewController(DOSDbContext db, ProductService productService)
    {
        _db = db;
        _productService = productService;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customer_id")?.Value ?? "0");

    // â”€â”€ Map customer review to DOS CustomerReview shape â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private object MapCustomerReview(Models.Catalog.ProductReview r)
    {
        return new
        {
            id = r.Id,
            name = r.Name,
            title = r.Title,
            comment = r.Comment,
            rating = r.Rating,
            images = Array.Empty<object>(),
            profile = (object?)null,
            created_at = r.CreatedAt?.ToString("MMM dd, yyyy"),
            product_id = r.ProductId,
            product_name = r.Product != null ? _productService.GetProductName(r.Product) : null
        };
    }

    /// <summary>List customer's own reviews</summary>
    [HttpGet]
    public async Task<IActionResult> GetCustomerReviews()
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var reviews = await _db.ProductReviews
            .Include(r => r.Product).ThenInclude(p => p!.AttributeValues)
            .Where(r => r.CustomerId == customerId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var data = reviews.Select(MapCustomerReview);

        return Ok(data);
    }

    /// <summary>Get a single customer review by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCustomerReview(int id)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var r = await _db.ProductReviews
            .Include(r => r.Product).ThenInclude(p => p!.AttributeValues)
            .FirstOrDefaultAsync(r => r.Id == id && r.CustomerId == customerId);

        if (r == null) return NotFound(new { message = "Review not found." });

        return Ok(MapCustomerReview(r));
    }
}
