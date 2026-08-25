using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Helpers;

namespace DOSApi.Controllers.Shop;

/// <summary>
/// Saves the customer's chosen delivery date + time slot for a cart that
/// requires preorder — see CheckoutService.SavePreorderSelectionAsync for
/// the validation (only a first-pass check; CreateOrderFromCartAsync
/// re-validates authoritatively at the moment the order is placed).
/// Routes: /api/shop/preorder
/// </summary>
[ApiController]
[Route("api/shop/preorder")]
[Tags("Preorder")]
[AllowAnonymous]
public class ShopSavePreorderSelectionController : ControllerBase
{
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly ExtraChargeService _extraChargeService;
    private readonly PreorderService _preorderService;
    private readonly CheckoutService _checkoutService;
    private readonly string _baseUrl;

    public ShopSavePreorderSelectionController(
        CartService cartService, AuthService authService, ExtraChargeService extraChargeService,
        PreorderService preorderService, CheckoutService checkoutService, IConfiguration config)
    {
        _cartService = cartService;
        _authService = authService;
        _extraChargeService = extraChargeService;
        _preorderService = preorderService;
        _checkoutService = checkoutService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    public record SavePreorderSelectionRequest(int RuleId, DateTime DeliveryDate, int SlotId);

    /// <summary>Save the chosen preorder delivery date + time slot for one
    /// distinct preorder group (rule) on the current cart.</summary>
    [HttpPost("save-selection")]
    public async Task<IActionResult> SaveSelection(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken,
        [FromBody] SavePreorderSelectionRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var (success, message) = await _checkoutService.SavePreorderSelectionAsync(cart.Id, req.RuleId, req.DeliveryDate, req.SlotId);
        if (!success)
            return BadRequest(new { message });

        var updatedCart = await _cartService.GetCartAsync(customerId, cartToken);
        if (updatedCart == null)
            return NotFound(new { message = "Cart not found." });

        var extraCharges = await _extraChargeService.ComputeAsync(updatedCart.Items);
        var pincode = updatedCart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? updatedCart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
        var preorder = await _preorderService.ResolveForCartAsync(updatedCart, pincode);

        return Ok(new
        {
            message,
            data = CartResourceHelper.ToCartResource(updatedCart, _baseUrl, extraCharges, preorder)
        });
    }

    /// <summary>The authoritative preorder charge — tax and coupon discount
    /// included, per group and combined — exactly what CreateRazorpayOrderAsync
    /// will actually charge for this cart's preorder items. The Review step
    /// calls this to show the real amount before the customer taps Pay,
    /// instead of a client-side estimate that can silently diverge once tax
    /// or a coupon is involved.</summary>
    [HttpGet("total")]
    public async Task<IActionResult> GetPreorderTotal([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null)
            return NotFound(new { message = "Cart not found." });

        var breakdown = await _checkoutService.ComputePreorderTotalsBreakdownAsync(cart);

        // Combined, cart-wide figures — the Review step shows one unified
        // breakdown (subtotal/charges/total) rather than repeating the same
        // line items once per group. Same-named extra charges from different
        // groups (e.g. "Handling Charges" computed independently per group)
        // are summed together rather than listed twice.
        var subTotal = breakdown.Groups.Sum(g => g.SubTotal);
        var taxAmount = breakdown.Groups.Sum(g => g.TaxAmount);
        var discountAmount = breakdown.Groups.Sum(g => g.DiscountAmount);
        var extraChargesTotal = breakdown.Groups.Sum(g => g.ExtraChargesTotal);
        var extraCharges = breakdown.Groups
            .SelectMany(g => g.ExtraChargeLines)
            .GroupBy(l => l.Name)
            .Select(g => (object)new
            {
                name = g.Key,
                amount = g.Sum(l => l.Amount),
                formatted_amount = CartResourceHelper.FormatPrice(g.Sum(l => l.Amount)),
            })
            .ToList();

        return Ok(new
        {
            sub_total = subTotal,
            formatted_sub_total = CartResourceHelper.FormatPrice(subTotal),
            tax_amount = taxAmount,
            formatted_tax_amount = CartResourceHelper.FormatPrice(taxAmount),
            discount_amount = discountAmount,
            formatted_discount_amount = CartResourceHelper.FormatPrice(discountAmount),
            extra_charges = extraCharges,
            extra_charges_total = extraChargesTotal,
            formatted_extra_charges_total = CartResourceHelper.FormatPrice(extraChargesTotal),
            grand_total = breakdown.GrandTotal,
            formatted_grand_total = CartResourceHelper.FormatPrice(breakdown.GrandTotal),
            groups = breakdown.Groups.Select(g => (object)new
            {
                rule_id = g.RuleId,
                sub_total = g.SubTotal,
                tax_amount = g.TaxAmount,
                discount_amount = g.DiscountAmount,
                grand_total = g.GrandTotal,
                formatted_grand_total = CartResourceHelper.FormatPrice(g.GrandTotal),
            }).ToList()
        });
    }
}
