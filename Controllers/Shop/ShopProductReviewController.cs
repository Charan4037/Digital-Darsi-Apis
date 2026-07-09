using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/reviews")]
[Tags("ProductReview")]
[AllowAnonymous]
public class ShopProductReviewController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly AuthService _authService;

    public ShopProductReviewController(DOSDbContext db, AuthService authService)
    {
        _db = db;
        _authService = authService;
    }

    private int GetCustomerId() => _authService.GetCurrentCustomerId() ?? 0;

    private static object MapReview(Models.Catalog.ProductReview r)
    {
        return new
        {
            r.Id,
            r.Name,
            r.Title,
            r.Comment,
            r.Rating,
            Images = Array.Empty<object>(),
            Profile = (object?)null,
            CreatedAt = r.CreatedAt?.ToString("MMM dd, yyyy")
        };
    }

    /// <summary>List approved reviews with optional product filter</summary>
    [HttpGet]
    public async Task<IActionResult> GetReviews([FromQuery] int? product_id)
    {
        var q = _db.ProductReviews.Where(r => r.Status == "approved").AsQueryable();

        if (product_id.HasValue)
            q = q.Where(r => r.ProductId == product_id);

        var reviews = await q
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return Ok(reviews.Select(MapReview).ToList());
    }

    /// <summary>Get a single review by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetReview(int id)
    {
        var r = await _db.ProductReviews.FindAsync(id);
        if (r == null) return NotFound(new { message = "Review not found." });
        return Ok(MapReview(r));
    }

    /// <summary>
    /// Check if the authenticated customer is eligible to review a product.
    /// Eligibility requires a completed order containing the product and no prior review.
    /// </summary>
    [HttpGet("eligibility")]
    [Authorize]
    public async Task<IActionResult> CheckEligibility([FromQuery] int productId)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var product = await _db.Products.FindAsync(productId);
        if (product == null) return NotFound(new { message = "Product not found." });

        var completedOrderIds = _db.Orders
            .Where(o => o.CustomerId == customerId &&
                        (o.Status == "completed" || o.Status == "complete" || o.Status == "delivered"))
            .Select(o => (int?)o.Id);

        var hasCompletedOrder = await _db.OrderItems
            .Where(i => i.ProductId == productId && completedOrderIds.Contains(i.OrderId))
            .AnyAsync();

        var existingReview = await _db.ProductReviews
            .FirstOrDefaultAsync(r => r.ProductId == productId && r.CustomerId == customerId);

        return Ok(new
        {
            canReview = hasCompletedOrder && existingReview == null,
            alreadyReviewed = existingReview != null,
            existingReviewId = existingReview?.Id,
            hasCompletedOrder
        });
    }

    /// <summary>
    /// Submit a review. Requires authentication and a completed order containing the product.
    /// Reviews are auto-approved and immediately visible on the product page.
    /// </summary>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateReview([FromBody] CreateReviewRequest request)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var product = await _db.Products.FindAsync(request.ProductId);
        if (product == null) return NotFound(new { message = "Product not found." });

        // Verify the customer has a completed order containing this product
        var completedOrderIds = _db.Orders
            .Where(o => o.CustomerId == customerId &&
                        (o.Status == "completed" || o.Status == "complete" || o.Status == "delivered"))
            .Select(o => (int?)o.Id);

        var hasCompletedOrder = await _db.OrderItems
            .Where(i => i.ProductId == request.ProductId && completedOrderIds.Contains(i.OrderId))
            .AnyAsync();

        if (!hasCompletedOrder)
            return BadRequest(new { message = "You can only review products from completed orders." });

        // Prevent duplicate reviews
        var alreadyReviewed = await _db.ProductReviews
            .AnyAsync(r => r.ProductId == request.ProductId && r.CustomerId == customerId);

        if (alreadyReviewed)
            return Conflict(new { message = "You have already reviewed this product." });

        // Resolve the reviewer's display name from their profile if not provided
        var reviewerName = request.Name;
        if (string.IsNullOrWhiteSpace(reviewerName))
        {
            var customer = await _db.Customers.FindAsync(customerId);
            if (customer != null)
                reviewerName = $"{customer.FirstName} {customer.LastName}".Trim();
        }

        var review = new Models.Catalog.ProductReview
        {
            ProductId = request.ProductId,
            Title = request.Title ?? "",
            Comment = request.Comment,
            Rating = Math.Clamp(request.Rating, 1, 5),
            Name = string.IsNullOrWhiteSpace(reviewerName) ? "Customer" : reviewerName,
            CustomerId = customerId,
            Status = "approved",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ProductReviews.Add(review);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Review submitted successfully.",
            data = MapReview(review)
        });
    }

    /// <summary>Update a review (owner only). Keeps the approved status.</summary>
    [HttpPatch("{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateReview(int id, [FromBody] UpdateReviewRequest request)
    {
        var review = await _db.ProductReviews.FindAsync(id);
        if (review == null) return NotFound(new { message = "Review not found." });

        var customerId = GetCustomerId();
        if (review.CustomerId != customerId) return Forbid();

        if (request.Title != null) review.Title = request.Title;
        if (request.Comment != null) review.Comment = request.Comment;
        if (request.Rating.HasValue) review.Rating = Math.Clamp(request.Rating.Value, 1, 5);
        if (request.Name != null) review.Name = request.Name;
        review.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Review updated successfully.",
            data = MapReview(review)
        });
    }

    /// <summary>Delete a review (owner only)</summary>
    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteReview(int id)
    {
        var review = await _db.ProductReviews.FindAsync(id);
        if (review == null) return NotFound(new { message = "Review not found." });

        var customerId = GetCustomerId();
        if (review.CustomerId != customerId) return Forbid();

        _db.ProductReviews.Remove(review);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Review deleted successfully." });
    }
}

public class CreateReviewRequest
{
    public int ProductId { get; set; }
    public string? Title { get; set; }
    public string? Comment { get; set; }
    public int Rating { get; set; }
    public string? Name { get; set; }
}

public class UpdateReviewRequest
{
    public string? Title { get; set; }
    public string? Comment { get; set; }
    public int? Rating { get; set; }
    public string? Name { get; set; }
}
