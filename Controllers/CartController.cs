using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/v1/checkout/cart")]
[Tags("Cart")]
[ApiExplorerSettings(IgnoreApi = true)]
public class CartController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly BagistoDbContext _db;

    public CartController(CartService cartService, AuthService authService, BagistoDbContext db)
    {
        _cartService = cartService;
        _authService = authService;
        _db = db;
    }

    public record AddToCartRequest(int ProductId, int Quantity = 1);
    public record UpdateCartRequest(int CartItemId, int Quantity);
    public record CouponRequest(string Code);
    public record MergeCartRequest(int CartId);
    public record SelectedItemsRequest(int[] Ids);
    public record MoveToWishlistRequest(int ProductId);

    /// <summary>Get current cart contents</summary>
    /// <remarks>GET /api/checkout/cart</remarks>
    [HttpGet]
    public async Task<IActionResult> GetCart([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return Ok(new { data = (object?)null, message = "Cart is empty." });

        return Ok(new { data = MapCart(cart) });
    }

    /// <summary>Add a product to the cart</summary>
    /// <remarks>POST /api/checkout/cart</remarks>
    [HttpPost]
    public async Task<IActionResult> AddToCart(
        [FromBody] AddToCartRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
        {
            var (newCart, token, _, _) = await _cartService.CreateCartAsync(customerId);
            cart = newCart;
        }

        var (updatedCart, success, message) = await _cartService.AddToCartAsync(cart, req.ProductId, req.Quantity);
        if (!success) return BadRequest(new { success, message });
        return Ok(new { success, message, data = MapCart(updatedCart!) });
    }

    /// <summary>Update cart item quantity</summary>
    /// <remarks>PUT /api/checkout/cart</remarks>
    [HttpPut]
    public async Task<IActionResult> UpdateCart(
        [FromBody] UpdateCartRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.UpdateCartItemAsync(cart, req.CartItemId, req.Quantity);
        if (!success) return BadRequest(new { success, message });
        return Ok(new { success, message, data = MapCart(updatedCart!) });
    }

    /// <summary>Clear the entire cart</summary>
    /// <remarks>DELETE /api/checkout/cart</remarks>
    [HttpDelete]
    public async Task<IActionResult> ClearCart([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        foreach (var item in cart.Items.ToList())
            await _cartService.RemoveCartItemAsync(cart, item.Id);

        return Ok(new { success = true, message = "Cart cleared." });
    }

    /// <summary>Delete selected cart items</summary>
    /// <remarks>DELETE /api/checkout/cart/selected</remarks>
    [HttpDelete("selected")]
    public async Task<IActionResult> DeleteSelectedItems(
        [FromBody] SelectedItemsRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        foreach (var id in req.Ids)
            await _cartService.RemoveCartItemAsync(cart, id);

        return Ok(new { success = true, message = "Selected items removed.", data = MapCart(cart) });
    }

    /// <summary>Move cart item to wishlist</summary>
    /// <remarks>POST /api/checkout/cart/move-to-wishlist</remarks>
    [HttpPost("move-to-wishlist")]
    [Authorize]
    public async Task<IActionResult> MoveToWishlist(
        [FromBody] MoveToWishlistRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        // Add to wishlist
        var exists = await _db.Wishlists.AnyAsync(w => w.CustomerId == customerId.Value && w.ProductId == req.ProductId);
        if (!exists)
        {
            _db.Wishlists.Add(new Models.Customer.Wishlist
            {
                CustomerId = customerId.Value,
                ProductId = req.ProductId,
                ChannelId = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Remove from cart
        var cartItem = cart.Items.FirstOrDefault(i => i.ProductId == req.ProductId);
        if (cartItem != null)
            await _cartService.RemoveCartItemAsync(cart, cartItem.Id);

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Item moved to wishlist." });
    }

    /// <summary>Apply a coupon code to the cart</summary>
    /// <remarks>POST /api/checkout/cart/coupon</remarks>
    [HttpPost("coupon")]
    public async Task<IActionResult> ApplyCoupon(
        [FromBody] CouponRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.ApplyCouponAsync(cart, req.Code);
        if (!success) return BadRequest(new { success, message });
        return Ok(new { success, message, data = MapCart(updatedCart!) });
    }

    /// <summary>Estimate shipping methods for cart</summary>
    /// <remarks>POST /api/checkout/cart/estimate-shipping-methods</remarks>
    [HttpPost("estimate-shipping-methods")]
    public IActionResult EstimateShippingMethods()
    {
        return Ok(new
        {
            data = new[]
            {
                new { method = "flatrate_flatrate", label = "Flat Rate", price = 0m, formattedPrice = "₹0.00" },
                new { method = "free_free", label = "Free Shipping", price = 0m, formattedPrice = "₹0.00" }
            }
        });
    }

    /// <summary>Remove applied coupon from the cart</summary>
    /// <remarks>DELETE /api/checkout/cart/coupon</remarks>
    [HttpDelete("coupon")]
    public async Task<IActionResult> RemoveCoupon([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.RemoveCouponAsync(cart);
        return Ok(new { success, message, data = MapCart(updatedCart!) });
    }

    /// <summary>Get cross-sell products for cart</summary>
    /// <remarks>GET /api/checkout/cart/cross-sell</remarks>
    [HttpGet("cross-sell")]
    public IActionResult GetCrossSell()
    {
        return Ok(new { data = Array.Empty<object>() });
    }

    private static object MapCart(Models.Cart.Cart cart) => new
    {
        cart.Id,
        cart.ItemsCount,
        cart.ItemsQty,
        cart.SubTotal,
        cart.TaxTotal,
        cart.DiscountAmount,
        cart.GrandTotal,
        cart.CouponCode,
        cart.ShippingMethod,
        Items = cart.Items.Select(i => new
        {
            i.Id,
            i.ProductId,
            i.Sku,
            i.Name,
            i.Type,
            i.Quantity,
            i.Price,
            i.Total,
            FormattedPrice = $"₹{i.Price:N2}",
            FormattedTotal = $"₹{i.Total:N2}"
        })
    };
}
