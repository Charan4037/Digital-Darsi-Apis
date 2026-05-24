using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/merge-carts")]
[Tags("MergeCart")]
[Authorize]
public class ShopMergeCartController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;

    public ShopMergeCartController(CartService cartService, AuthService authService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
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

        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(cart!, _baseUrl)
        });
    }
}
