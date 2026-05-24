using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/update-cart-item")]
[Tags("UpdateCartItem")]
[AllowAnonymous]
public class ShopUpdateCartItemController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;

    public ShopUpdateCartItemController(CartService cartService, AuthService authService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    public record UpdateCartItemRequest(int CartItemId, int Quantity);

    /// <summary>Update cart item quantity</summary>
    [HttpPost]
    public async Task<IActionResult> UpdateCartItem(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] UpdateCartItemRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.UpdateCartItemAsync(cart, req.CartItemId, req.Quantity);

        if (!success)
            return BadRequest(new { message });

        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl)
        });
    }
}
