using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/newsletters")]
[Tags("Newsletter")]
[AllowAnonymous]
public class ShopNewsletterController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopNewsletterController(BagistoDbContext db)
    {
        _db = db;
    }

    public record SubscribeRequest(string Email);

    /// <summary>Subscribe to newsletter</summary>
    [HttpPost]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { message = "The email field is required." });

        var existing = await _db.NewsletterSubscribers.FirstOrDefaultAsync(n => n.Email == req.Email);
        if (existing != null)
        {
            if (existing.IsSubscribed)
                return Ok(new { message = "You are already subscribed to our newsletter." });

            existing.IsSubscribed = true;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { message = "You are now subscribed to our newsletter." });
        }

        var subscriber = new NewsletterSubscriber
        {
            Email = req.Email,
            IsSubscribed = true,
            ChannelId = 1,
            Token = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.NewsletterSubscribers.Add(subscriber);
        await _db.SaveChangesAsync();

        return Ok(new { message = "You are now subscribed to our newsletter." });
    }
}
