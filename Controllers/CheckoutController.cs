using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/v1/checkout/onepage")]
[Tags("Checkout")]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize]
public class CheckoutController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly AuthService _authService;

    public CheckoutController(CheckoutService checkoutService, CartService cartService, AuthService authService)
    {
        _checkoutService = checkoutService;
        _cartService = cartService;
        _authService = authService;
    }

    public record CheckoutAddressRequest(
        string FirstName, string LastName, string Address, string City,
        string State, string Country, string Postcode, string Phone,
        string? Email, bool UseForShipping = true, bool DefaultAddress = false);

    public record ShippingMethodRequest(string ShippingMethod);
    public record PaymentMethodRequest(string PaymentMethod);

    /// <summary>Get checkout summary (current cart)</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        return Ok(new
        {
            data = new
            {
                cart.Id,
                cart.ItemsCount,
                cart.ItemsQty,
                cart.SubTotal,
                cart.TaxTotal,
                cart.DiscountAmount,
                cart.GrandTotal,
                cart.CouponCode,
                cart.ShippingMethod,
                Items = cart.Items.Select(i => new { i.Id, i.ProductId, i.Name, i.Quantity, i.Price, i.Total })
            }
        });
    }

    /// <summary>Save billing/shipping address during checkout</summary>
    [HttpPost("addresses")]
    public async Task<IActionResult> SaveAddress(
        [FromBody] CheckoutAddressRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (success, message, addressId) = await _checkoutService.SaveCheckoutAddressAsync(
            cart.Id, req.FirstName, req.LastName, req.Address, req.City,
            req.State, req.Country, req.Postcode, req.Phone, req.Email,
            req.UseForShipping, req.DefaultAddress, customerId);

        return Ok(new { success, message, addressId });
    }

    /// <summary>Get available shipping methods</summary>
    [HttpGet("shipping-methods")]
    public IActionResult GetShippingMethods()
    {
        return Ok(new { data = _checkoutService.GetShippingRates() });
    }

    /// <summary>Select shipping method</summary>
    [HttpPost("shipping-methods")]
    public async Task<IActionResult> SaveShippingMethod(
        [FromBody] ShippingMethodRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (success, message) = await _checkoutService.SaveShippingMethodAsync(cart.Id, req.ShippingMethod);
        return Ok(new { success, message });
    }

    /// <summary>Get available payment methods</summary>
    [HttpGet("payment-methods")]
    public IActionResult GetPaymentMethods()
    {
        return Ok(new { data = _checkoutService.GetPaymentMethods() });
    }

    /// <summary>Select payment method</summary>
    [HttpPost("payment-methods")]
    public async Task<IActionResult> SavePaymentMethod(
        [FromBody] PaymentMethodRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var (success, message, gatewayUrl, paymentData) = await _checkoutService.SavePaymentMethodAsync(cart.Id, req.PaymentMethod);
        return Ok(new { success, message, gatewayUrl, paymentData });
    }

    /// <summary>Place order from current cart</summary>
    [HttpPost("orders")]
    public async Task<IActionResult> PlaceOrder([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId();
        var cart = await _cartService.GetCartAsync(customerId, cartToken);
        if (cart == null) return NotFound(new { success = false, message = "Cart not found." });

        var guestSession = customerId == null ? cartToken : null;
        var (success, message, orderId, incrementId) = await _checkoutService.PlaceOrderAsync(
            cart.Id, customerId, guestSessionToken: guestSession);
        if (!success) return BadRequest(new { success, message });

        return Ok(new { success, message, orderId, orderNumber = incrementId });
    }
}
