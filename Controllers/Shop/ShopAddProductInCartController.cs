using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/add-product-in-cart")]
[Tags("AddProductInCart")]
[AllowAnonymous]
public class ShopAddProductInCartController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly string _baseUrl;

    public ShopAddProductInCartController(CartService cartService, AuthService authService, ExtraChargeService extraChargeService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
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

        bool cartWasCreated = false;
        if (cart == null)
        {
            var (newCart, token, _, _) = await _cartService.CreateCartAsync(customerId);
            cart = newCart;
            cartToken = token;
            cartWasCreated = true;
        }

        var (updatedCart, success, message) = await _cartService.AddToCartAsync(cart, req.ProductId, req.Quantity);

        if (!success)
            return BadRequest(new { message });

        // For guest carts always issue (or re-issue) the HMAC session token.
        // Include it in both the response header AND the body so the client
        // receives it even when a proxy or middleware strips response headers.
        string? sessionToken = null;
        if (updatedCart!.CustomerId == null)
        {
            sessionToken = cartWasCreated && cartToken != null
                ? cartToken
                : _cartService.IssueGuestCartToken(updatedCart.Id);
        }

        if (sessionToken != null)
            Response.Headers["X-Cart-Token"] = sessionToken;

        var extraCharges = await _extraChargeService.ComputeAsync(updatedCart!.Items);
        return Ok(new
        {
            message,
            cart_token = sessionToken,
            data = CartResourceHelper.ToCartResource(updatedCart!, _baseUrl, extraCharges)
        });
    }
}
