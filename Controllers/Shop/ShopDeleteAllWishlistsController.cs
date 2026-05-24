using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/delete-all-wishlists")]
[Tags("DeleteAllWishlists")]
[Authorize]
public class ShopDeleteAllWishlistsController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopDeleteAllWishlistsController(BagistoDbContext db)
    {
        _db = db;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customer_id")?.Value ?? "0");

    /// <summary>Clear all wishlist items for the current customer</summary>
    [HttpPost]
    public async Task<IActionResult> DeleteAllWishlists()
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var items = await _db.Wishlists
            .Where(w => w.CustomerId == customerId)
            .ToListAsync();

        if (items.Count == 0)
            return Ok(new { message = "Wishlist is already empty." });

        _db.Wishlists.RemoveRange(items);
        await _db.SaveChangesAsync();

        return Ok(new { message = "All wishlist items removed successfully." });
    }
}
