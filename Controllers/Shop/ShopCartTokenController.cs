using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/cart-tokens")]
[Tags("CartToken")]
[AllowAnonymous]
public class ShopCartTokenController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly string _baseUrl;

    public ShopCartTokenController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    /// <summary>Get cart by token or auth</summary>
    [HttpGet]
    public async Task<IActionResult> GetCart(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
            return Ok((object?)null);

        var extraCharges = await _extraChargeService.ComputeAsync(cart.Items);
        return Ok(CartResourceHelper.ToCartResource(cart, _baseUrl, extraCharges));
    }

    /// <summary>Get specific cart by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCartById(int id,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null || cart.Id != id)
            return NotFound(new { message = "Cart not found." });

        var extraCharges = await _extraChargeService.ComputeAsync(cart.Items);
        return Ok(CartResourceHelper.ToCartResource(cart, _baseUrl, extraCharges));
    }
}
