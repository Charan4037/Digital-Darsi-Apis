using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/cart-tokens")]
[Tags("CartToken")]
[AllowAnonymous]
public class ShopCartTokenController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;

    public ShopCartTokenController(CartService cartService, AuthService authService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
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

        return Ok(CartResourceHelper.ToCartResource(cart, _baseUrl));
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

        return Ok(CartResourceHelper.ToCartResource(cart, _baseUrl));
    }
}
