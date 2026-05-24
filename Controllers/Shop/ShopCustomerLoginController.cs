using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer")]
[Tags("CustomerLogin")]
[AllowAnonymous]
public class ShopCustomerLoginController : ControllerBase
{
    private readonly AuthService _authService;

    public ShopCustomerLoginController(AuthService authService)
    {
        _authService = authService;
    }

    public record LoginRequest(string Email, string Password);

    /// <summary>Customer login</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var result = await _authService.LoginAsync(req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return Unauthorized(new { message = result.Message });

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
            message = "Logged in successfully.",
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
}
