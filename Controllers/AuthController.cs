using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;

namespace DOSApi.Controllers;

[ApiController]
[Route("api/v1")]
[Tags("Authentication")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly IConfiguration _config;

    public AuthController(AuthService auth, IConfiguration config)
    {
        _auth = auth;
        _config = config;
    }

    public record LoginRequest(string Email, string Password);
    public record RegisterRequest(string FirstName, string LastName, string Email, string Password);
    public record ForgotPasswordRequest(string Email);
    public record RefreshTokenRequest(string RefreshToken);
    public record LogoutRequest(string? RefreshToken);
    public record FirebaseLoginRequest(string IdToken, string? FirstName, string? LastName);
    public record OtpTestLoginRequest(string PhoneNumber, string? FirstName, string? LastName);

    /// <summary>Customer login with email + password — returns access + refresh tokens.</summary>
    [HttpPost("customer/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var result = await _auth.LoginAsync(req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return Unauthorized(new { success = false, message = result.Message });

        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Register a new customer — returns access + refresh tokens.</summary>
    [HttpPost("customer/register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var result = await _auth.RegisterAsync(req.FirstName, req.LastName, req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return BadRequest(new { success = false, message = result.Message });

        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Send password reset link to the customer's email.</summary>
    [HttpPost("customer/forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        var (message, success) = await _auth.ForgotPasswordAsync(req.Email);
        if (!success) return NotFound(new { success, message });
        return Ok(new { success, message });
    }

    /// <summary>
    /// [Testing only] Returns a JWT token for an existing customer's phone number. Requires X-Admin-Key header.
    /// Accepts 10-digit numbers (e.g. 8088214037) or full E.164 format (e.g. +918088214037).
    /// </summary>
    [HttpPost("customer/otp/test-login")]
    [AllowAnonymous]
    public async Task<IActionResult> OtpTestLogin(
        [FromBody] OtpTestLoginRequest req,
        [FromHeader(Name = "X-Admin-Key")] string? adminKey)
    {
        var expectedKey = _config["Admin:NotificationApiKey"];
        if (string.IsNullOrWhiteSpace(adminKey) || adminKey != expectedKey)
            return Unauthorized(new { success = false, message = "Valid X-Admin-Key header is required." });

        var result = await _auth.TestLoginByPhoneAsync(req.PhoneNumber, req.FirstName, req.LastName);
        if (result.Success && result.Customer != null && result.Tokens != null)
            return Ok(BuildAuthResponse(result));

        return BadRequest(new { success = false, message = result.Message });
    }

    /// <summary>Phone OTP login — used by the mobile app. Pass the Firebase ID token obtained after phone verification.</summary>
    [HttpPost("customer/firebase-login")]
    [AllowAnonymous]
    public async Task<IActionResult> FirebaseLogin([FromBody] FirebaseLoginRequest req)
    {
        var result = await _auth.LoginWithFirebaseAsync(req.IdToken, req.FirstName, req.LastName);
        if (result.Success && result.Customer != null && result.Tokens != null)
            return Ok(BuildAuthResponse(result));

        if (result.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            return StatusCode(503, new { success = false, message = result.Message });
        if (result.Message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            result.Message.Contains("no phone", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = result.Message });
        return Unauthorized(new { success = false, message = result.Message });
    }

    /// <summary>
    /// Exchange a refresh token for a fresh access + refresh token pair.
    /// Call this when the access token expires (30 min) instead of forcing the user to log in again.
    /// </summary>
    [HttpPost("customer/refresh-token")]
    [AllowAnonymous]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest req)
    {
        var result = await _auth.RefreshAsync(req.RefreshToken);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return Unauthorized(new { success = false, message = result.Message });

        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Logout — revokes the supplied refresh token. The access token remains valid until it expires.</summary>
    [HttpPost("customer/logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? req)
    {
        if (!string.IsNullOrWhiteSpace(req?.RefreshToken))
            await _auth.RevokeRefreshTokenAsync(req.RefreshToken);

        return Ok(new { success = true, message = "Logged out successfully." });
    }

    /// <summary>Logout from every device — revokes all refresh tokens for the current customer.</summary>
    [HttpPost("customer/logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var customerId = _auth.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var revoked = await _auth.RevokeAllForCustomerAsync(customerId.Value);
        return Ok(new { success = true, message = "Logged out of all sessions.", revoked });
    }

    private static object BuildAuthResponse(AuthService.AuthResult result)
    {
        var customer = result.Customer!;
        var tokens = result.Tokens!;
        return new
        {
            success = true,
            message = result.Message,
            isNewUser = result.IsNewUser,
            token = tokens.AccessToken,
            tokenType = "Bearer",
            accessToken = tokens.AccessToken,
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt,
            refreshToken = tokens.RefreshToken,
            refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt,
            data = new
            {
                customer.Id,
                customer.FirstName,
                customer.LastName,
                customer.Email,
                customer.Phone,
                customer.Gender,
                customer.DateOfBirth
            }
        };
    }
}
