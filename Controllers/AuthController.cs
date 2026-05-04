using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/v1")]
[Tags("Authentication")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth) => _auth = auth;

    public record LoginRequest(string Email, string Password);
    public record RegisterRequest(string FirstName, string LastName, string Email, string Password);
    public record ForgotPasswordRequest(string Email);

    /// <summary>Customer login</summary>
    [HttpPost("customer/login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var (customer, token, message, success) = await _auth.LoginAsync(req.Email, req.Password);
        if (!success) return Unauthorized(new { success, message });
        return Ok(new
        {
            success,
            message,
            token,
            data = new
            {
                customer!.Id,
                customer.FirstName,
                customer.LastName,
                customer.Email,
                customer.Phone,
                customer.Gender,
                customer.DateOfBirth
            }
        });
    }

    /// <summary>Register a new customer</summary>
    [HttpPost("customer/register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var (customer, token, message, success) = await _auth.RegisterAsync(req.FirstName, req.LastName, req.Email, req.Password);
        if (!success) return BadRequest(new { success, message });
        return Ok(new
        {
            success,
            message,
            token,
            data = new
            {
                customer!.Id,
                customer.FirstName,
                customer.LastName,
                customer.Email
            }
        });
    }

    /// <summary>Send password reset link</summary>
    [HttpPost("customer/forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        var (message, success) = await _auth.ForgotPasswordAsync(req.Email);
        if (!success) return NotFound(new { success, message });
        return Ok(new { success, message });
    }

    /// <summary>Logout customer</summary>
    [HttpPost("customer/logout")]
    [Authorize]
    public IActionResult Logout()
    {
        return Ok(new { success = true, message = "Logged out successfully." });
    }
}
