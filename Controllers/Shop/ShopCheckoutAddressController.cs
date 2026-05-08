using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;
using BagistoApi.Models.Customer;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/checkout-addresses")]
[Tags("CheckoutAddress")]
[Authorize]
public class ShopCheckoutAddressController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopCheckoutAddressController(
        CheckoutService checkoutService,
        CartService cartService,
        AuthService authService,
        BagistoDbContext db,
        IConfiguration config)
    {
        _checkoutService = checkoutService;
        _cartService = cartService;
        _authService = authService;
        _db = db;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
    }

    public record SaveCheckoutAddressRequest(
        string FirstName, string LastName, string Address, string City,
        string State, string Country, string Postcode, string Phone,
        string? Email, bool UseForShipping = true, bool DefaultAddress = false);

    /// <summary>Get checkout addresses for the current cart</summary>
    [HttpGet]
    public async Task<IActionResult> GetCheckoutAddresses(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var addresses = await _db.Addresses
            .Where(a => a.CartId == cart.Id)
            .OrderBy(a => a.Id)
            .ToListAsync();

        var data = addresses.Select(FormatAddress);

        return Ok(data);
    }

    /// <summary>Get a single checkout address by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCheckoutAddress(
        int id,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var address = await _db.Addresses
            .Where(a => a.CartId == cart.Id && a.Id == id)
            .FirstOrDefaultAsync();

        if (address == null)
            return NotFound(new { message = "Address not found." });

        return Ok(FormatAddress(address));
    }

    /// <summary>Save billing/shipping address during checkout</summary>
    [HttpPost]
    public async Task<IActionResult> SaveCheckoutAddress(
        [FromBody] SaveCheckoutAddressRequest req,
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var (success, message, addressId) = await _checkoutService.SaveCheckoutAddressAsync(
            cart.Id, req.FirstName, req.LastName, req.Address, req.City,
            req.State, req.Country, req.Postcode, req.Phone, req.Email,
            req.UseForShipping, req.DefaultAddress, customerId > 0 ? customerId : null);

        if (!success)
            return BadRequest(new { message });

        // Return the saved billing address in AddressResource format
        var saved = await _db.Addresses.FindAsync(addressId);

        return Ok(new
        {
            data = saved != null ? FormatAddress(saved) : null,
            message
        });
    }

    private static object FormatAddress(Address a) => new
    {
        id = a.Id,
        address_type = a.AddressType,
        parent_address_id = a.ParentAddressId,
        customer_id = a.CustomerId,
        cart_id = a.CartId,
        order_id = a.OrderId,
        first_name = a.FirstName,
        last_name = a.LastName,
        gender = a.Gender,
        company_name = a.CompanyName,
        address = (a.AddressLine ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries),
        city = a.City,
        state = a.State,
        country = a.Country,
        postcode = a.Postcode,
        email = a.Email,
        phone = a.Phone,
        vat_id = a.VatId,
        default_address = a.DefaultAddress ? 1 : 0,
        use_for_shipping = a.UseForShipping ? 1 : 0,
        additional = a.Additional
    };
}
