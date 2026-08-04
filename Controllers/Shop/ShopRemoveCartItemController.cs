using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/remove-cart-item")]
[Tags("RemoveCartItem")]
[AllowAnonymous]
public class ShopRemoveCartItemController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly string _baseUrl;

    public ShopRemoveCartItemController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    public record RemoveCartItemRequest(int CartItemId);

    /// <summary>Remove a single item from cart</summary>
    [HttpPost]
    public async Task<IActionResult> RemoveCartItem(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] RemoveCartItemRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.RemoveCartItemAsync(cart, req.CartItemId);

        if (!success)
            return BadRequest(new { message });

        var extraCharges = await _extraChargeService.ComputeAsync(updatedCart!.Items);
        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl, extraCharges)
        });
    }
}
