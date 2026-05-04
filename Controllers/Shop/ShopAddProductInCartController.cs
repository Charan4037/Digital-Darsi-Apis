using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/add-product-in-cart")]
[Tags("AddProductInCart")]
public class ShopAddProductInCartController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;

    public ShopAddProductInCartController(CartService cartService, AuthService authService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    public record AddToCartRequest(
        int ProductId,
        int Quantity = 1,
        object? Options = null,
        object? BundleOptions = null,
        object? BundleOptionQty = null,
        object? GroupedQty = null,
        object? Booking = null,
        string? SpecialNote = null
    );

    /// <summary>Add product to cart</summary>
    [HttpPost]
    public async Task<IActionResult> AddProductToCart(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] AddToCartRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
        {
            var (newCart, token, _, _) = await _cartService.CreateCartAsync(customerId);
            cart = newCart;
            cartToken = token;
        }

        var (updatedCart, success, message) = await _cartService.AddToCartAsync(cart, req.ProductId, req.Quantity);

        if (!success)
            return BadRequest(new { message });

        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl)
        });
    }
}
