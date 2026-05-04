using Microsoft.AspNetCore.Mvc;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/checkout-shipping-methods")]
[Tags("CheckoutShippingMethod")]
public class ShopCheckoutShippingMethodController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopCheckoutShippingMethodController(
        CheckoutService checkoutService,
        CartService cartService,
        BagistoDbContext db,
        IConfiguration config)
    {
        _checkoutService = checkoutService;
        _cartService = cartService;
        _db = db;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
    }

    public record SaveShippingMethodRequest(string ShippingMethod);

    /// <summary>Get available shipping methods (grouped by carrier)</summary>
    [HttpGet]
    public IActionResult GetShippingMethods()
    {
        var rates = _checkoutService.GetShippingRates();

        // Group rates by carrier to match Bagisto shipping method response format
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
                    base_discount_amount = 0
                })
            })
            .ToList();

        return Ok(grouped);
    }

    /// <summary>Get a single shipping method by method code</summary>
    [HttpGet("{id}")]
    public IActionResult GetShippingMethod(string id)
    {
        var rates = _checkoutService.GetShippingRates();
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
                    base_discount_amount = 0
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
        var customerId = int.Parse(User.FindFirst("customerId")?.Value ?? "0");
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var (success, message) = await _checkoutService.SaveShippingMethodAsync(cart.Id, req.ShippingMethod);

        if (!success)
            return BadRequest(new { message });

        // Return the available shipping methods after saving, matching Bagisto format
        var rates = _checkoutService.GetShippingRates();
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
                    base_discount_amount = 0
                })
            });

        return Ok(new
        {
            data = grouped,
            message
        });
    }
}
