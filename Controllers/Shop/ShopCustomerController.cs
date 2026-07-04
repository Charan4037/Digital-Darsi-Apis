using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customers")]
[Tags("Customer")]
public class ShopCustomerController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly AccountService _accountService;

    public ShopCustomerController(AuthService authService, AccountService accountService)
    {
        _authService = authService;
        _accountService = accountService;
    }

    public record CustomerRegisterRequest(string FirstName, string LastName, string Email, string Password);
    public record UpdateCustomerRequest(string? FirstName, string? LastName, string? Phone, string? Gender,
        string? DateOfBirth, string? Email, string? CurrentPassword, string? NewPassword, bool? Newsletter);
    public record DeleteCustomerRequest(string Password);

    /// <summary>List customers (returns current customer only)</summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> ListCustomers()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var customer = await _accountService.GetProfileAsync(customerId);
        if (customer == null) return NotFound(new { message = "Customer not found." });

        var data = new[] { FormatCustomerProfile(customer) };

        return Ok(data);
    }

    /// <summary>Register new customer</summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] CustomerRegisterRequest req)
    {
        var result = await _authService.RegisterAsync(
            req.FirstName, req.LastName, req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return BadRequest(new { message = result.Message });

        var customer = result.Customer;
        var tokens = result.Tokens;

        return Ok(new
        {
            token = tokens.AccessToken,
            tokenType = "Bearer",
            accessToken = tokens.AccessToken,
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt,
            refreshToken = tokens.RefreshToken,
            refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt,
            message = "Registered successfully.",
            data = new
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
                image_url = (string?)null
            }
        });
    }

    /// <summary>Get customer by ID</summary>
    [HttpGet("{id:int}")]
    [Authorize]
    public async Task<IActionResult> GetCustomer(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var customer = await _accountService.GetProfileAsync(id);
        if (customer == null) return NotFound(new { message = "Customer not found." });

        return Ok(FormatCustomerProfile(customer));
    }

    /// <summary>Update customer</summary>
    [HttpPut("{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateCustomer(int id, [FromBody] UpdateCustomerRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        await _accountService.UpdateProfileAsync(id, req.FirstName, req.LastName,
            req.Phone, req.Gender, req.DateOfBirth, req.Newsletter);

        if (!string.IsNullOrEmpty(req.Email) && !string.IsNullOrEmpty(req.CurrentPassword))
        {
            var (success, message) = await _accountService.ChangeEmailAsync(id, req.Email, req.CurrentPassword);
            if (!success) return BadRequest(new { message });
        }

        if (!string.IsNullOrEmpty(req.NewPassword) && !string.IsNullOrEmpty(req.CurrentPassword))
        {
            var (success, message) = await _accountService.ChangePasswordAsync(id, req.CurrentPassword, req.NewPassword);
            if (!success) return BadRequest(new { message });
        }

        var updated = await _accountService.GetProfileAsync(id);
        return Ok(new
        {
            message = "Your account has been updated successfully.",
            data = updated != null ? FormatCustomerProfile(updated) : null
        });
    }

    /// <summary>Delete customer</summary>
    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteCustomer(int id, [FromBody] DeleteCustomerRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var (success, message) = await _accountService.DeleteAccountAsync(id, req.Password);
        if (!success) return BadRequest(new { message });
        return Ok(new { message });
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
