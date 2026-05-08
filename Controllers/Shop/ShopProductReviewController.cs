using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/reviews")]
[Tags("ProductReview")]
[AllowAnonymous]
public class ShopProductReviewController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly AuthService _authService;

    public ShopProductReviewController(BagistoDbContext db, AuthService authService)
    {
        _db = db;
        _authService = authService;
    }

    private int GetCustomerId() => _authService.GetCurrentCustomerId() ?? 0;

    // -- Map review to Bagisto ProductReviewResource shape --
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

    /// <summary>List all reviews with optional product filter</summary>
    [HttpGet]
    public async Task<IActionResult> GetReviews([FromQuery] int? product_id)
    {
        var q = _db.ProductReviews.AsQueryable();

        if (product_id.HasValue)
            q = q.Where(r => r.ProductId == product_id);

        var reviews = await q
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var data = reviews.Select(MapReview).ToList();

        return Ok(data);
    }

    /// <summary>Get a single review by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetReview(int id)
    {
        var r = await _db.ProductReviews.FindAsync(id);
        if (r == null) return NotFound(new { message = "Review not found." });

        return Ok(MapReview(r));
    }

    /// <summary>Create a new review</summary>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateReview([FromBody] CreateReviewRequest request)
    {
        var product = await _db.Products.FindAsync(request.ProductId);
        if (product == null) return NotFound(new { message = "Product not found." });

        var customerId = GetCustomerId();

        var review = new Models.Catalog.ProductReview
        {
            ProductId = request.ProductId,
            Title = request.Title,
            Comment = request.Comment,
            Rating = request.Rating,
            Name = request.Name ?? "Guest",
            CustomerId = customerId > 0 ? customerId : null,
            Status = "pending",
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

    /// <summary>Update a review (owner only)</summary>
    [HttpPatch("{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateReview(int id, [FromBody] UpdateReviewRequest request)
    {
        var review = await _db.ProductReviews.FindAsync(id);
        if (review == null) return NotFound(new { message = "Review not found." });

        var customerId = GetCustomerId();
        if (review.CustomerId != customerId)
            return Forbid();

        if (request.Title != null) review.Title = request.Title;
        if (request.Comment != null) review.Comment = request.Comment;
        if (request.Rating.HasValue) review.Rating = request.Rating.Value;
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
        if (review.CustomerId != customerId)
            return Forbid();

        _db.ProductReviews.Remove(review);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Review deleted successfully." });
    }
}

public class CreateReviewRequest
{
    public int ProductId { get; set; }
    public string Title { get; set; } = "";
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
