using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Helpers;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// The regular (non-preorder) bucket's own authoritative checkout total —
/// the counterpart to ShopSavePreorderSelectionController's "preorder/total"
/// endpoint. A mixed cart can now be checked out in either order (see
/// CheckoutService.CreateOrderFromCartAsync/BuildPreorderOrdersFromCartAsync),
/// so the Review step can no longer assume the cart's own whole-cart totals
/// (CartModel.subTotal/grandTotal/extraCharges) represent just the regular
/// items — they may still include preorder items sitting untouched in the
/// same cart, waiting on their own, separate checkout.
/// Routes: /api/shop/regular-items
/// </summary>
[ApiController]
[Route("api/shop/regular-items")]
[Tags("Checkout")]
[AllowAnonymous]
public class ShopRegularItemsController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly CheckoutService _checkoutService;

    public ShopRegularItemsController(CartService cartService, AuthService authService, CheckoutService checkoutService)
    {
        _cartService = cartService;
        _authService = authService;
        _checkoutService = checkoutService;
    }

    /// <summary>The authoritative charge for just the cart's regular items —
    /// tax, coupon discount, extra charges and shipping included — exactly
    /// what CreateRazorpayOrderAsync's non-preorder-only path and
    /// CreateOrderFromCartAsync will actually charge/order. The Review step
    /// calls this to show the real amount before the customer taps Pay or
    /// Place Order, instead of the cart's own combined totals.</summary>
    [HttpGet("total")]
    public async Task<IActionResult> GetRegularItemsTotal([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var pincode = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
        var totals = await _checkoutService.ComputeRegularItemsTotalsAsync(cart, pincode);

        return Ok(new
        {
            item_count = totals.ItemCount,
            sub_total = totals.SubTotal,
            formatted_sub_total = CartResourceHelper.FormatPrice(totals.SubTotal),
            tax_amount = totals.TaxAmount,
            formatted_tax_amount = CartResourceHelper.FormatPrice(totals.TaxAmount),
            discount_amount = totals.DiscountAmount,
            formatted_discount_amount = CartResourceHelper.FormatPrice(totals.DiscountAmount),
            shipping_amount = totals.ShippingAmount,
            formatted_shipping_amount = CartResourceHelper.FormatPrice(totals.ShippingAmount),
            shipping_title = totals.ShippingTitle,
            shipping_description = totals.ShippingDescription,
            extra_charges = totals.ExtraChargeLines.Select(l => (object)new
            {
                name = l.Name,
                amount = l.Amount,
                formatted_amount = CartResourceHelper.FormatPrice(l.Amount),
            }).ToList(),
            extra_charges_total = totals.ExtraChargesTotal,
            formatted_extra_charges_total = CartResourceHelper.FormatPrice(totals.ExtraChargesTotal),
            grand_total = totals.GrandTotal,
            formatted_grand_total = CartResourceHelper.FormatPrice(totals.GrandTotal),
        });
    }
}
