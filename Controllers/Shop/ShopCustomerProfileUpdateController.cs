using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Models.Customer;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-profile-updates")]
[Tags("CustomerProfileUpdate")]
[Authorize]
public class ShopCustomerProfileUpdateController : ControllerBase
{
    private readonly AccountService _accountService;
    private readonly DOSDbContext _db;

    public ShopCustomerProfileUpdateController(AccountService accountService, DOSDbContext db)
    {
        _accountService = accountService;
        _db = db;
    }

    public record ProfileUpdateRequest(string? FirstName, string? LastName, string? Phone,
        string? Gender, string? DateOfBirth, bool? Newsletter, string? Email);

    /// <summary>Update customer profile</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateProfile(int id, [FromBody] ProfileUpdateRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        if (req.Email != null)
        {
            var emailTaken = await _db.Customers.AnyAsync(c => c.Email == req.Email && c.Id != id);
            if (emailTaken)
                return BadRequest(new { message = "This email address is already in use." });
        }

        var updated = await _accountService.UpdateProfileAsync(id, req.FirstName, req.LastName,
            req.Phone, req.Gender, req.DateOfBirth, req.Newsletter, req.Email);

        if (!updated)
            return NotFound(new { message = "Customer not found." });

        var customer = await _accountService.GetProfileAsync(id);
        var isAdmin = customer != null && await _db.CustomerAdmins.AnyAsync(a => a.CustomerId == customer.Id);

        return Ok(new
        {
            message = "Your account has been updated successfully.",
            data = customer != null ? FormatCustomerProfile(customer, isAdmin) : null
        });
    }

    private static object FormatCustomerProfile(Customer customer, bool isAdmin) => new
    {
        id = customer.Id,
        first_name = customer.FirstName,
        last_name = customer.LastName,
        name = $"{customer.FirstName} {customer.LastName}",
        email = customer.Email,
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
