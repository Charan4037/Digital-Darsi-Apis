using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using Serilog;

namespace DOSApi.Controllers;

[ApiController]
[Route("api/v1")]
[Tags("Authentication")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AuthService auth, IConfiguration config, ILogger<AuthController> logger)
    {
        _auth = auth;
        _config = config;
        _logger = logger;
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
        _logger.LogInformation("Login attempt for email: {Email}", req.Email);
        var result = await _auth.LoginAsync(req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
        {
            _logger.LogWarning("Login failed for email: {Email}, Message: {Message}", req.Email, result.Message);
            return Unauthorized(new { success = false, message = result.Message });
        }

        _logger.LogInformation("Login successful for customer ID: {CustomerId}", result.Customer.Id);
        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Register a new customer — returns access + refresh tokens.</summary>
    [HttpPost("customer/register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        _logger.LogInformation("Registration attempt for email: {Email}, Name: {FirstName} {LastName}", req.Email, req.FirstName, req.LastName);
        var result = await _auth.RegisterAsync(req.FirstName, req.LastName, req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
        {
            _logger.LogWarning("Registration failed for email: {Email}, Message: {Message}", req.Email, result.Message);
            return BadRequest(new { success = false, message = result.Message });
        }

        _logger.LogInformation("Registration successful for new customer ID: {CustomerId}, Email: {Email}", result.Customer.Id, result.Customer.Email);
        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Send password reset link to the customer's email.</summary>
    [HttpPost("customer/forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        _logger.LogInformation("Forgot password request for email: {Email}", req.Email);
        var (message, success) = await _auth.ForgotPasswordAsync(req.Email);
        if (!success)
        {
            _logger.LogWarning("Forgot password failed for email: {Email}", req.Email);
            return NotFound(new { success, message });
        }
        _logger.LogInformation("Forgot password email sent to: {Email}", req.Email);
        return Ok(new { success, message });
    }

    /// <summary>
    /// [Testing only] Returns a JWT token for an existing customer's phone number. Requires X-Admin-Key header.
    /// </summary>
    [HttpPost("customer/otp/test-login")]
    [AllowAnonymous]
    public async Task<IActionResult> OtpTestLogin(
        [FromBody] OtpTestLoginRequest req,
        [FromHeader(Name = "X-Admin-Key")] string? adminKey)
    {
        _logger.LogInformation("OTP test login attempt for phone: {Phone}", req.PhoneNumber);
        var expectedKey = _config["Admin:NotificationApiKey"];
        if (string.IsNullOrWhiteSpace(adminKey) || adminKey != expectedKey)
        {
            _logger.LogWarning("OTP test login failed - invalid admin key for phone: {Phone}", req.PhoneNumber);
            return Unauthorized(new { success = false, message = "Valid X-Admin-Key header is required." });
        }

        var result = await _auth.TestLoginByPhoneAsync(req.PhoneNumber, req.FirstName, req.LastName);
        if (result.Success && result.Customer != null && result.Tokens != null)
        {
            _logger.LogInformation("OTP test login successful for customer ID: {CustomerId}, Phone: {Phone}", result.Customer.Id, req.PhoneNumber);
            return Ok(BuildAuthResponse(result));
        }

        _logger.LogWarning("OTP test login failed for phone: {Phone}, Message: {Message}", req.PhoneNumber, result.Message);
        return BadRequest(new { success = false, message = result.Message });
    }

    /// <summary>Phone OTP login — used by the mobile app. Pass the Firebase ID token obtained after phone verification.</summary>
    [HttpPost("customer/firebase-login")]
    [AllowAnonymous]
    public async Task<IActionResult> FirebaseLogin([FromBody] FirebaseLoginRequest req)
    {
        _logger.LogInformation("Firebase login attempt");
        var result = await _auth.LoginWithFirebaseAsync(req.IdToken, req.FirstName, req.LastName);
        if (result.Success && result.Customer != null && result.Tokens != null)
        {
            _logger.LogInformation("Firebase login successful for customer ID: {CustomerId}", result.Customer.Id);
            return Ok(BuildAuthResponse(result));
        }

        if (result.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Firebase login failed - Firebase not configured: {Message}", result.Message);
            return StatusCode(503, new { success = false, message = result.Message });
        }
        if (result.Message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            result.Message.Contains("no phone", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Firebase login failed - invalid request: {Message}", result.Message);
            return BadRequest(new { success = false, message = result.Message });
        }
        _logger.LogWarning("Firebase login failed - unauthorized: {Message}", result.Message);
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
        _logger.LogInformation("Token refresh attempt");
        var result = await _auth.RefreshAsync(req.RefreshToken);
        if (!result.Success || result.Customer == null || result.Tokens == null)
        {
            _logger.LogWarning("Token refresh failed: {Message}", result.Message);
            return Unauthorized(new { success = false, message = result.Message });
        }

        _logger.LogInformation("Token refresh successful for customer ID: {CustomerId}", result.Customer.Id);
        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Logout — revokes the supplied refresh token. The access token remains valid until it expires.</summary>
    [HttpPost("customer/logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? req)
    {
        var customerId = _auth.GetCurrentCustomerId();
        _logger.LogInformation("Logout request for customer ID: {CustomerId}", customerId);
        if (!string.IsNullOrWhiteSpace(req?.RefreshToken))
        {
            var revoked = await _auth.RevokeRefreshTokenAsync(req.RefreshToken);
            _logger.LogInformation("Refresh token revoked for customer ID: {CustomerId}, Success: {Revoked}", customerId, revoked);
        }

        _logger.LogInformation("Logout successful for customer ID: {CustomerId}", customerId);
        return Ok(new { success = true, message = "Logged out successfully." });
    }

    /// <summary>Logout from every device — revokes all refresh tokens for the current customer.</summary>
    [HttpPost("customer/logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var customerId = _auth.GetCurrentCustomerId();
        _logger.LogInformation("Logout-all request");
        if (!customerId.HasValue)
        {
            _logger.LogWarning("Logout-all failed - customer ID not found in claims");
            return Unauthorized();
        }

        var revoked = await _auth.RevokeAllForCustomerAsync(customerId.Value);
        _logger.LogInformation("Logout-all successful for customer ID: {CustomerId}, Tokens revoked: {Revoked}", customerId.Value, revoked);
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
