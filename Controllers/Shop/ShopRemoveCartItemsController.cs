using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/remove-cart-items")]
[Tags("RemoveCartItems")]
public class ShopRemoveCartItemsController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly string _baseUrl;

    public ShopRemoveCartItemsController(CartService cartService, AuthService authService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    public record RemoveCartItemsRequest(List<int>? ItemIds = null);

    /// <summary>Remove specific items (or all items) from cart</summary>
    [HttpPost]
    public async Task<IActionResult> RemoveCartItems(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] RemoveCartItemsRequest? req = null)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);

        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        // If itemIds provided, remove only those; otherwise remove all
        var idsToRemove = req?.ItemIds != null && req.ItemIds.Count > 0
            ? req.ItemIds
            : cart.Items.Select(i => i.Id).ToList();

        foreach (var itemId in idsToRemove)
        {
            await _cartService.RemoveCartItemAsync(cart, itemId);
        }

        return Ok(new
        {
            message = "Items removed from cart successfully.",
            data = CartResourceHelper.ToCartResource(cart, _baseUrl)
        });
    }
}
