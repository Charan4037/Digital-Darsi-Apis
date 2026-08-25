using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/merge-carts")]
[Tags("MergeCart")]
[Authorize]
public class ShopMergeCartController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly PreorderService _preorderService;
    private readonly string _baseUrl;

    public ShopMergeCartController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, PreorderService preorderService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _preorderService = preorderService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    /// <summary>Merge guest cart into customer cart</summary>
    [HttpPost("{id:int}")]
    public async Task<IActionResult> MergeCart(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue)
            return Unauthorized(new { message = "Authentication required." });

        var (cart, success, message, _) = await _cartService.MergeCartAsync(id, customerId.Value);

        if (!success)
            return BadRequest(new { message });

        var extraCharges = await _extraChargeService.ComputeAsync(cart!.Items);
        var pincode = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
        var preorder = await _preorderService.ResolveForCartAsync(cart, pincode);
        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(cart!, _baseUrl, extraCharges, preorder)
        });
    }
}
