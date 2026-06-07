using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Models.Customer;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-profiles")]
[Tags("CustomerProfile")]
[Authorize]
public class ShopCustomerProfileController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCustomerProfileController(AccountService accountService)
    {
        _accountService = accountService;
    }

    /// <summary>Get current customer profile</summary>
    [HttpGet]
    public async Task<IActionResult> GetProfile()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var customer = await _accountService.GetProfileAsync(customerId);
        if (customer == null) return NotFound(new { message = "Customer not found." });

        var data = new[] { FormatCustomerProfile(customer) };

        return Ok(data);
    }

    /// <summary>Get customer profile by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetProfileById(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var customer = await _accountService.GetProfileAsync(id);
        if (customer == null) return NotFound(new { message = "Customer not found." });

        return Ok(FormatCustomerProfile(customer));
    }

    private static object FormatCustomerProfile(Customer customer) => new
    {
        id = customer.Id,
        first_name = customer.FirstName,
        last_name = customer.LastName,
        name = $"{customer.FirstName} {customer.LastName}",
        email = customer.Email != null && customer.Email.EndsWith("@digitaldarsi.local")
            ? null
            : customer.Email,
        phone = customer.Phone,
        gender = customer.Gender,
        date_of_birth = customer.DateOfBirth?.ToString("yyyy-MM-dd"),
        subscribed_to_news_letter = customer.SubscribedToNewsLetter ? 1 : 0,
        image = customer.Image,
        image_url = (string?)null,
        created_at = customer.CreatedAt,
        updated_at = customer.UpdatedAt
    };
}
