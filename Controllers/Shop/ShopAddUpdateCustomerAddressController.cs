using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Services;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-address-add-updates")]
[Tags("AddUpdateCustomerAddress")]
[Authorize]
public class ShopAddUpdateCustomerAddressController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopAddUpdateCustomerAddressController(AccountService accountService)
    {
        _accountService = accountService;
    }

    public record AddressRequest(string FirstName, string LastName, string Address, string City,
        string State, string Country, string Postcode, string Phone, string? Email,
        bool DefaultAddress = false);

    /// <summary>List customer addresses</summary>
    [HttpGet]
    public async Task<IActionResult> GetAddresses()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var addresses = await _accountService.GetAddresses(customerId).ToListAsync();

        var data = addresses.Select(FormatAddress);

        return Ok(data);
    }

    /// <summary>Get single address</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAddress(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var address = await _accountService.GetAddresses(customerId)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (address == null)
            return NotFound(new { message = "Address not found." });

        return Ok(FormatAddress(address));
    }

    /// <summary>Create a new address</summary>
    [HttpPost]
    public async Task<IActionResult> CreateAddress([FromBody] AddressRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var addr = await _accountService.AddOrUpdateAddressAsync(customerId, null,
            req.FirstName, req.LastName, req.Address, req.City, req.State,
            req.Country, req.Postcode, req.Phone, req.Email, false, req.DefaultAddress);

        return Ok(new
        {
            message = "Address created successfully.",
            data = FormatAddress(addr)
        });
    }

    /// <summary>Update an existing address</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAddress(int id, [FromBody] AddressRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var addr = await _accountService.AddOrUpdateAddressAsync(customerId, id,
            req.FirstName, req.LastName, req.Address, req.City, req.State,
            req.Country, req.Postcode, req.Phone, req.Email, false, req.DefaultAddress);

        return Ok(new
        {
            message = "Address updated successfully.",
            data = FormatAddress(addr)
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
