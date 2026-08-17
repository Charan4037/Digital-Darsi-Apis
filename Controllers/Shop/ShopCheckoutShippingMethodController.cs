using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/checkout-shipping-methods")]
[Tags("CheckoutShippingMethod")]
[Authorize]
public class ShopCheckoutShippingMethodController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly DOSDbContext _db;
    private readonly string _baseUrl;

    public ShopCheckoutShippingMethodController(
        CheckoutService checkoutService,
        CartService cartService,
        AuthService authService,
        DOSDbContext db,
        IConfiguration config)
    {
        _checkoutService = checkoutService;
        _cartService = cartService;
        _authService = authService;
        _db = db;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
    }

    public record SaveShippingMethodRequest(string ShippingMethod);

    /// <summary>Get available shipping methods (grouped by carrier)</summary>
    [HttpGet]
    public async Task<IActionResult> GetShippingMethods([FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        var rates = await _checkoutService.GetShippingRatesAsync(cart?.Id);

        // Group rates by carrier to match DOS shipping method response format
        var grouped = rates
            .GroupBy(r => r.Carrier)
            .Select(g => new
            {
                carrier_code = g.Key,
                carrier_title = g.First().CarrierTitle,
                rates = g.Select(r => new
                {
                    id = r.Id,
                    carrier = r.Carrier,
                    carrier_title = r.CarrierTitle,
                    method = r.Method,
                    method_title = r.MethodTitle,
                    method_description = r.Description,
                    price = r.Price,
                    formatted_price = r.FormattedPrice,
                    base_price = r.BasePrice,
                    formatted_base_price = r.BaseFormattedPrice,
                    discount_amount = 0,
                    base_discount_amount = 0,
                    delivery_hours = r.DeliveryHours
                })
            })
            .ToList();

        return Ok(grouped);
    }

    /// <summary>Get a single shipping method by method code</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetShippingMethod(string id, [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        var rates = await _checkoutService.GetShippingRatesAsync(cart?.Id);
        var rate = rates.FirstOrDefault(r => r.Method == id || r.Code == id);
        if (rate == null)
            return NotFound(new { message = "Shipping method not found." });

        return Ok(new
        {
            carrier_code = rate.Carrier,
            carrier_title = rate.CarrierTitle,
            rates = new[]
            {
                new
                {
                    id = rate.Id,
                    carrier = rate.Carrier,
                    carrier_title = rate.CarrierTitle,
                    method = rate.Method,
                    method_title = rate.MethodTitle,
                    method_description = rate.Description,
                    price = rate.Price,
                    formatted_price = rate.FormattedPrice,
                    base_price = rate.BasePrice,
                    formatted_base_price = rate.BaseFormattedPrice,
                    discount_amount = 0,
                    base_discount_amount = 0,
                    delivery_hours = rate.DeliveryHours
                }
            }
        });
    }

    /// <summary>Save selected shipping method for the cart</summary>
    [HttpPost]
    public async Task<IActionResult> SaveShippingMethod(
        [FromBody] SaveShippingMethodRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var (success, message) = await _checkoutService.SaveShippingMethodAsync(cart.Id, req.ShippingMethod);

        if (!success)
            return BadRequest(new { message });

        // Return the available shipping methods after saving, matching DOS format
        var rates = await _checkoutService.GetShippingRatesAsync(cart.Id);
        var grouped = rates
            .GroupBy(r => r.Carrier)
            .Select(g => new
            {
                carrier_code = g.Key,
                carrier_title = g.First().CarrierTitle,
                rates = g.Select(r => new
                {
                    id = r.Id,
                    carrier = r.Carrier,
                    carrier_title = r.CarrierTitle,
                    method = r.Method,
                    method_title = r.MethodTitle,
                    method_description = r.Description,
                    price = r.Price,
                    formatted_price = r.FormattedPrice,
                    base_price = r.BasePrice,
                    formatted_base_price = r.BaseFormattedPrice,
                    discount_amount = 0,
                    base_discount_amount = 0,
                    delivery_hours = r.DeliveryHours
                })
            });

        return Ok(new
        {
            data = grouped,
            message
        });
    }
}
