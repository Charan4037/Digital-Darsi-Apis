using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;
using BagistoApi.Models.Customer;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-profile-updates")]
[Tags("CustomerProfileUpdate")]
[Authorize]
public class ShopCustomerProfileUpdateController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCustomerProfileUpdateController(AccountService accountService)
    {
        _accountService = accountService;
    }

    public record ProfileUpdateRequest(string? FirstName, string? LastName, string? Phone,
        string? Gender, string? DateOfBirth, bool? Newsletter);

    /// <summary>Update customer profile</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateProfile(int id, [FromBody] ProfileUpdateRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var updated = await _accountService.UpdateProfileAsync(id, req.FirstName, req.LastName,
            req.Phone, req.Gender, req.DateOfBirth, req.Newsletter);

        if (!updated)
            return NotFound(new { message = "Customer not found." });

        var customer = await _accountService.GetProfileAsync(id);

        return Ok(new
        {
            message = "Your account has been updated successfully.",
            data = customer != null ? FormatCustomerProfile(customer) : null
        });
    }

    private static object FormatCustomerProfile(Customer customer) => new
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
        updated_at = customer.UpdatedAt
    };
}
