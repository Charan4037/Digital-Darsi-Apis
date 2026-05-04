using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/reviews")]
[Tags("ProductReview")]
public class ShopProductReviewController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopProductReviewController(BagistoDbContext db)
    {
        _db = db;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customerId")?.Value ?? "0");

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

    /// <summary>Update a review</summary>
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> UpdateReview(int id, [FromBody] UpdateReviewRequest request)
    {
        var review = await _db.ProductReviews.FindAsync(id);
        if (review == null) return NotFound(new { message = "Review not found." });

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

    /// <summary>Delete a review</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteReview(int id)
    {
        var review = await _db.ProductReviews.FindAsync(id);
        if (review == null) return NotFound(new { message = "Review not found." });

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
