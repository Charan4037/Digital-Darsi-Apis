using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-profiles")]
[Tags("CustomerProfile")]
[Authorize]
public class ShopCustomerProfileController : ControllerBase
{
    private readonly AccountService _accountService;
    private readonly DOSDbContext _db;

    public ShopCustomerProfileController(AccountService accountService, DOSDbContext db)
    {
        _accountService = accountService;
        _db = db;
    }

    /// <summary>Get current customer profile</summary>
    [HttpGet]
    public async Task<IActionResult> GetProfile()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var customer = await _accountService.GetProfileAsync(customerId);
        if (customer == null) return NotFound(new { message = "Customer not found." });

        var isAdmin = await _db.CustomerAdmins.AnyAsync(a => a.CustomerId == customer.Id);
        var data = new[] { FormatCustomerProfile(customer, isAdmin) };

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

        var isAdmin = await _db.CustomerAdmins.AnyAsync(a => a.CustomerId == customer.Id);
        return Ok(FormatCustomerProfile(customer, isAdmin));
    }

    private static object FormatCustomerProfile(Customer customer, bool isAdmin) => new
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
        updated_at = customer.UpdatedAt,
        is_admin = isAdmin
    };
}
