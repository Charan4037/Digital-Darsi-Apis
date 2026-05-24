using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Helpers;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/remove-cart-items")]
[Tags("RemoveCartItems")]
[AllowAnonymous]
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
            return Ok(new
            {
                message = "Cart already empty.",
                data = new
                {
                    id = 0,
                    is_guest = false,
                    customer_id = (int?)null,
                    items_count = 0,
                    items_qty = 0m,
                    applied_taxes = new { },
                    tax_total = 0m,
                    formatted_tax_total = "$0.00",
                    sub_total_incl_tax = 0m,
                    sub_total = 0m,
                    formatted_sub_total_incl_tax = "$0.00",
                    formatted_sub_total = "$0.00",
                    coupon_code = (string?)null,
                    discount_amount = 0m,
                    formatted_discount_amount = "$0.00",
                    shipping_method = (string?)null,
                    shipping_amount = 0m,
                    formatted_shipping_amount = "$0.00",
                    shipping_amount_incl_tax = 0m,
                    formatted_shipping_amount_incl_tax = "$0.00",
                    grand_total = 0m,
                    formatted_grand_total = "$0.00",
                    items = Array.Empty<object>(),
                    billing_address = (object?)null,
                    shipping_address = (object?)null,
                    have_stockable_items = false,
                    payment_method = (string?)null,
                    payment_method_title = (string?)null
                }
            });

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
