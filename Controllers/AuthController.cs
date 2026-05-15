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
    public record RefreshTokenRequest(string RefreshToken);
    public record LogoutRequest(string? RefreshToken);
    public record FirebaseLoginRequest(string IdToken, string? FirstName, string? LastName);

    /// <summary>Customer login — returns an access token + refresh token.</summary>
    [HttpPost("customer/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var result = await _auth.LoginAsync(req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return Unauthorized(new { success = false, message = result.Message });

        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Register a new customer — returns an access token + refresh token.</summary>
    [HttpPost("customer/register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var result = await _auth.RegisterAsync(req.FirstName, req.LastName, req.Email, req.Password);
        if (!result.Success || result.Customer == null || result.Tokens == null)
            return BadRequest(new { success = false, message = result.Message });

        return Ok(BuildAuthResponse(result));
    }

    /// <summary>Send password reset link.</summary>
    [HttpPost("customer/forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        var (message, success) = await _auth.ForgotPasswordAsync(req.Email);
        if (!success) return NotFound(new { success, message });
        return Ok(new { success, message });
    }

    /// <summary>
    /// Phone-OTP login. The mobile app handles the OTP exchange via
    /// Firebase Phone Auth and forwards us the resulting Firebase ID
    /// token. We verify it, find-or-create a customer by phone, and
    /// issue our normal access+refresh JWT pair so the rest of the API
    /// behaves identically to email/password login.
    ///
    /// Returns 503 if Firebase isn't configured on this server (e.g. no
    /// service-account JSON), 401 if the token is rejected, 400 if it
    /// has no phone-number claim.
    /// </summary>
    [HttpPost("customer/firebase-login")]
    [AllowAnonymous]
    public async Task<IActionResult> FirebaseLogin([FromBody] FirebaseLoginRequest req)
    {
        var result = await _auth.LoginWithFirebaseAsync(req.IdToken, req.FirstName, req.LastName);
        if (result.Success && result.Customer != null && result.Tokens != null)
            return Ok(BuildAuthResponse(result));

        // Map service-level reasons to HTTP shapes the client can branch on.
        if (result.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            return StatusCode(503, new { success = false, message = result.Message });
        if (result.Message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            result.Message.Contains("no phone", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = result.Message });
        return Unauthorized(new { success = false, message = result.Message });
    }

    /// <summary>
    /// Exchange a refresh token for a fresh access + refresh token pair.
    /// Anonymous on purpose — the access token is already expired by the
    /// time the client calls this, so we can't require a Bearer header.
    /// Security comes from the opaque refresh token itself.
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

    /// <summary>
    /// Logout — revokes the supplied refresh token (or none if the client
    /// just wants to drop its access token). The access token remains valid
    /// until it expires; clients should also forget it locally.
    /// </summary>
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
            // Legacy field — older Flutter clients still read `token`. New
            // clients should read `accessToken` instead.
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
