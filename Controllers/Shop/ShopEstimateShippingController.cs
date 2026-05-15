using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/estimate-shippings")]
[Tags("EstimateShipping")]
[AllowAnonymous]
public class ShopEstimateShippingController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopEstimateShippingController(
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

    /// <summary>Get estimated shipping rates (grouped by carrier)</summary>
    [HttpGet]
    public IActionResult GetEstimateShipping()
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

    /// <summary>Get a single shipping rate by method code</summary>
    [HttpGet("{id}")]
    public IActionResult GetEstimateShippingRate(string id)
    {
        var rates = _checkoutService.GetShippingRates();
        var rate = rates.FirstOrDefault(r => r.Method == id || r.Code == id);
        if (rate == null)
            return NotFound(new { message = "Shipping rate not found." });

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
}
