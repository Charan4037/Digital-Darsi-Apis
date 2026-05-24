using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/apply-coupon")]
[Tags("ApplyCoupon")]
[AllowAnonymous]
public class ShopApplyCouponController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;

    public ShopApplyCouponController(CartService cartService, AuthService authService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    public record ApplyCouponRequest(string CouponCode);

    /// <summary>Apply coupon to cart</summary>
    [HttpPost]
    public async Task<IActionResult> ApplyCoupon(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] ApplyCouponRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var (updatedCart, success, message) = await _cartService.ApplyCouponAsync(cart, req.CouponCode);

        if (!success)
            return BadRequest(new { message });

        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl)
        });
    }
}
