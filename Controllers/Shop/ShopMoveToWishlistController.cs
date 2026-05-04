using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;
using BagistoApi.Models.Customer;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/move-to-wishlists")]
[Tags("MoveToWishlist")]
[Authorize]
public class ShopMoveToWishlistController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly CartService _cartService;

    public ShopMoveToWishlistController(BagistoDbContext db, CartService cartService)
    {
        _db = db;
        _cartService = cartService;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customerId")?.Value ?? "0");

    /// <summary>Move a cart item to wishlist</summary>
    [HttpPost("{id:int}")]
    public async Task<IActionResult> MoveToWishlist(
        int id,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var cartItem = cart.Items.FirstOrDefault(i => i.Id == id);
        if (cartItem == null) return NotFound(new { message = "Cart item not found." });

        // Add to wishlist if not already present
        var exists = await _db.Wishlists.AnyAsync(w => w.CustomerId == customerId && w.ProductId == cartItem.ProductId);
        if (!exists)
        {
            var wishlist = new Wishlist
            {
                CustomerId = customerId,
                ProductId = cartItem.ProductId,
                ChannelId = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Wishlists.Add(wishlist);
        }

        // Remove from cart
        await _cartService.RemoveCartItemAsync(cart, id);

        return Ok(new { message = "Item moved to wishlist successfully." });
    }
}
