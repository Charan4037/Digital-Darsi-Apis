using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/remove-coupon")]
[Tags("RemoveCoupon")]
[AllowAnonymous]
public class ShopRemoveCouponController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly string _baseUrl;

    public ShopRemoveCouponController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    /// <summary>Remove coupon from cart</summary>
    [HttpPost]
    public async Task<IActionResult> RemoveCoupon(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.RemoveCouponAsync(cart);

        if (!success)
            return BadRequest(new { message });

        var extraCharges = await _extraChargeService.ComputeAsync(updatedCart!.SubTotal ?? 0m);
        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl, extraCharges)
        });
    }
}
