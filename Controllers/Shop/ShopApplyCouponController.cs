using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/apply-coupon")]
[Tags("ApplyCoupon")]
[AllowAnonymous]
public class ShopApplyCouponController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly PreorderService _preorderService;
    private readonly string _baseUrl;

    public ShopApplyCouponController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, PreorderService preorderService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _preorderService = preorderService;
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

        var extraCharges = await _extraChargeService.ComputeAsync(updatedCart!.Items);
        var pincode = updatedCart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? updatedCart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
        var preorder = await _preorderService.ResolveForCartAsync(updatedCart, pincode);
        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl, extraCharges, preorder)
        });
    }
}
