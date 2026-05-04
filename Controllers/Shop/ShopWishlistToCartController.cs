using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/move-wishlist-to-carts")]
[Tags("WishlistToCart")]
[Authorize]
public class ShopWishlistToCartController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly CartService _cartService;

    public ShopWishlistToCartController(BagistoDbContext db, CartService cartService)
    {
        _db = db;
        _cartService = cartService;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customerId")?.Value ?? "0");

    public record MoveWishlistToCartRequest(int WishlistItemId, int Quantity = 1);

    /// <summary>Move wishlist item to cart</summary>
    [HttpPost]
    public async Task<IActionResult> MoveToCart(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] MoveWishlistToCartRequest req)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var wishlistItem = await _db.Wishlists
            .FirstOrDefaultAsync(w => w.Id == req.WishlistItemId && w.CustomerId == customerId);

        if (wishlistItem == null)
            return NotFound(new { message = "Wishlist item not found." });

        // Get or create cart
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null)
        {
            var (newCart, token, _, _) = await _cartService.CreateCartAsync(customerId);
            cart = newCart;
        }

        // Add product to cart
        var (updatedCart, success, message) = await _cartService.AddToCartAsync(cart, wishlistItem.ProductId, req.Quantity);
        if (!success)
            return BadRequest(new { message });

        // Remove from wishlist
        _db.Wishlists.Remove(wishlistItem);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Wishlist item moved to cart successfully." });
    }
}
