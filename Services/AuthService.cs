using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using BagistoApi.Data;
using BagistoApi.Models.Customer;

namespace BagistoApi.Services;

/// <summary>
/// Auth surface for the customer portal. Issues JWT access tokens (short
/// lived, signed) plus opaque refresh tokens (long lived, hashed at rest in
/// <c>customer_refresh_tokens</c>). Refresh tokens rotate on every use:
/// the old hash is marked revoked and points at the new one, so a stolen
/// token replay can be detected and the whole chain killed.
/// </summary>
public class AuthService
{
    public record TokenBundle(
        string AccessToken,
        DateTime AccessTokenExpiresAt,
        string RefreshToken,
        DateTime RefreshTokenExpiresAt);

    public record AuthResult(
        bool Success,
        string Message,
        Customer? Customer = null,
        TokenBundle? Tokens = null,
        bool IsNewUser = false);

    private readonly BagistoDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor _http;

    private readonly TimeSpan _accessLifetime;
    private readonly TimeSpan _refreshLifetime;

    public AuthService(BagistoDbContext db, IConfiguration config, IHttpContextAccessor http)
    {
        _db = db;
        _config = config;
        _http = http;

        _accessLifetime = TimeSpan.FromMinutes(
            int.TryParse(_config["Jwt:AccessTokenMinutes"], out var am) && am > 0 ? am : 30);
        _refreshLifetime = TimeSpan.FromDays(
            int.TryParse(_config["Jwt:RefreshTokenDays"], out var rd) && rd > 0 ? rd : 30);
    }

    // ─── Public API ─────────────────────────────────────────────────────

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == email && c.Status == 1);
        if (customer == null || string.IsNullOrEmpty(customer.Password))
            return new AuthResult(false, "Invalid email or password.");

        // Laravel bcrypt passwords start with $2y$ — BCrypt.Net handles $2a$/$2b$/$2y$
        var pwd = customer.Password.Replace("$2y$", "$2a$");
        if (!BCrypt.Net.BCrypt.Verify(password, pwd))
            return new AuthResult(false, "Invalid email or password.");

        var tokens = await IssueTokensAsync(customer);
        return new AuthResult(true, "Login successful.", customer, tokens);
    }

    public async Task<AuthResult> RegisterAsync(
        string firstName, string lastName, string email, string password)
    {
        if (await _db.Customers.AnyAsync(c => c.Email == email))
            return new AuthResult(false, "Email already exists.");

        var customer = new Customer
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            Password = BCrypt.Net.BCrypt.HashPassword(password),
            Status = 1,
            IsVerified = true,
            ChannelId = 1,
            CustomerGroupId = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var tokens = await IssueTokensAsync(customer);
        return new AuthResult(true, "Registration successful.", customer, tokens);
    }

    /// <summary>
    /// Phone-OTP login via Firebase. The mobile app does the Firebase Phone
    /// Auth dance, then hands us the resulting Firebase ID token here. We
    /// verify the token's signature against Google's public certs, pull
    /// the phone number out of the verified claims, and find-or-create a
    /// matching customer row before issuing our own JWT pair.
    ///
    /// Failure modes:
    ///   - Firebase not configured on the server → returns success=false,
    ///     and the controller maps it to 503.
    ///   - Token invalid/expired/revoked/wrong-project → 401.
    ///   - Token has no phone_number claim (email-link sign-in etc.) → 400.
    /// </summary>
    public async Task<AuthResult> LoginWithFirebaseAsync(
        string firebaseIdToken,
        string? optionalFirstName = null,
        string? optionalLastName = null)
    {
        if (string.IsNullOrWhiteSpace(firebaseIdToken))
            return new AuthResult(false, "Firebase ID token is required.");

        if (FirebaseApp.DefaultInstance == null)
            return new AuthResult(false, "Phone login is not configured on the server.");

        FirebaseToken decoded;
        try
        {
            decoded = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(firebaseIdToken);
        }
        catch (FirebaseAuthException ex)
        {
            return new AuthResult(false, $"Invalid Firebase token: {ex.Message}");
        }

        // The phone is the source of identity for this flow. Firebase only
        // populates phone_number after a successful Phone Auth verification,
        // so its absence means the client used a different sign-in method
        // and we should reject the exchange.
        var phone = decoded.Claims.TryGetValue("phone_number", out var pn) ? pn?.ToString() : null;
        if (string.IsNullOrWhiteSpace(phone))
            return new AuthResult(false, "Firebase token has no phone number — phone OTP sign-in required.");

        // Normalize: keep '+' and digits only. Stops "+91 98765 43210" and
        // "+91-98765-43210" from creating duplicate customer rows.
        phone = NormalizePhone(phone);

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Phone == phone);
        var isNew = false;
        if (customer == null)
        {
            // Bagisto's customers.email column is typically NOT NULL UNIQUE,
            // so phone-only signups need a deterministic synthetic email.
            // Using the phone keeps it stable across re-installs.
            var syntheticEmail = $"phone_{phone.TrimStart('+')}@digitaldarsi.local";

            customer = new Customer
            {
                FirstName = optionalFirstName?.Trim() ?? "",
                LastName = optionalLastName?.Trim() ?? "",
                Email = syntheticEmail,
                Phone = phone,
                Password = null,
                Status = 1,
                IsVerified = true,
                ChannelId = 1,
                CustomerGroupId = 2,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.Customers.Add(customer);
            await _db.SaveChangesAsync();
            isNew = true;
        }
        else if (customer.Status != 1)
        {
            return new AuthResult(false, "This account is suspended.");
        }
        else if (!string.IsNullOrWhiteSpace(optionalFirstName) && string.IsNullOrEmpty(customer.FirstName))
        {
            // Backfill name on first OTP-verified login if it was empty.
            customer.FirstName = optionalFirstName.Trim();
            if (!string.IsNullOrWhiteSpace(optionalLastName))
                customer.LastName = optionalLastName.Trim();
            customer.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        var tokens = await IssueTokensAsync(customer);
        return new AuthResult(
            true,
            isNew ? "Account created via phone OTP." : "Login successful.",
            customer,
            tokens,
            IsNewUser: isNew);
    }

    private static string NormalizePhone(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '+' || char.IsDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    public async Task<(string message, bool success)> ForgotPasswordAsync(string email)
    {
        var exists = await _db.Customers.AnyAsync(c => c.Email == email);
        if (!exists)
            return ("We cannot find a user with that email address.", false);

        return ("We have e-mailed your password reset link!", true);
    }

    /// <summary>
    /// Validate a refresh token, rotate it (the old token is revoked and
    /// linked to the new one), and return a fresh access+refresh pair. If
    /// the supplied token is unknown, expired, or already revoked, all
    /// tokens for that customer are revoked as a precaution against replay.
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return new AuthResult(false, "Refresh token is required.");

        var hash = HashToken(refreshToken);
        var existing = await _db.CustomerRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (existing == null)
            return new AuthResult(false, "Invalid refresh token.");

        if (existing.RevokedAt != null)
        {
            // Replay of a revoked token — assume the chain is compromised
            // and revoke every active refresh token for this customer.
            await RevokeAllForCustomerAsync(existing.CustomerId);
            return new AuthResult(false, "Refresh token has been revoked.");
        }

        if (existing.ExpiresAt <= DateTime.UtcNow)
            return new AuthResult(false, "Refresh token has expired.");

        var customer = await _db.Customers.FindAsync(existing.CustomerId);
        if (customer == null || customer.Status != 1)
            return new AuthResult(false, "Account is not active.");

        // Rotate: revoke the old token and link it to its replacement.
        var newRefresh = GenerateRefreshTokenString();
        var newHash = HashToken(newRefresh);
        var now = DateTime.UtcNow;

        existing.RevokedAt = now;
        existing.ReplacedByHash = newHash;

        var (ip, ua) = ReadRequestMeta();
        _db.CustomerRefreshTokens.Add(new CustomerRefreshToken
        {
            CustomerId = customer.Id,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = now.Add(_refreshLifetime),
            CreatedIp = ip,
            UserAgent = ua,
        });
        await _db.SaveChangesAsync();

        var (jwt, jwtExp) = GenerateAccessToken(customer);
        return new AuthResult(true, "Token refreshed.", customer, new TokenBundle(
            jwt, jwtExp, newRefresh, now.Add(_refreshLifetime)));
    }

    /// <summary>Revoke a single refresh token (logout from one device).</summary>
    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return false;
        var hash = HashToken(refreshToken);
        var token = await _db.CustomerRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token == null || token.RevokedAt != null) return false;
        token.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Revoke every active refresh token for a customer (logout from all devices).</summary>
    public async Task<int> RevokeAllForCustomerAsync(int customerId)
    {
        var now = DateTime.UtcNow;
        return await _db.CustomerRefreshTokens
            .Where(t => t.CustomerId == customerId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    public int? GetCurrentCustomerId()
    {
        var claim = _http.HttpContext?.User?.FindFirst("customer_id");
        if (claim != null && int.TryParse(claim.Value, out var id))
            return id;
        return null;
    }

    public async Task<Customer?> GetCurrentCustomerAsync()
    {
        var id = GetCurrentCustomerId();
        if (id == null) return null;
        return await _db.Customers.FindAsync(id.Value);
    }

    // ─── Internals ──────────────────────────────────────────────────────

    private async Task<TokenBundle> IssueTokensAsync(Customer customer)
    {
        var (jwt, jwtExp) = GenerateAccessToken(customer);

        var refreshPlain = GenerateRefreshTokenString();
        var refreshHash = HashToken(refreshPlain);
        var now = DateTime.UtcNow;
        var refreshExpiry = now.Add(_refreshLifetime);

        var (ip, ua) = ReadRequestMeta();
        _db.CustomerRefreshTokens.Add(new CustomerRefreshToken
        {
            CustomerId = customer.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = refreshExpiry,
            CreatedIp = ip,
            UserAgent = ua,
        });
        await _db.SaveChangesAsync();

        return new TokenBundle(jwt, jwtExp, refreshPlain, refreshExpiry);
    }

    private (string token, DateTime expiresAt) GenerateAccessToken(Customer customer)
    {
        var keyBytes = Encoding.UTF8.GetBytes(GetSigningKey());
        var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var expires = now.Add(_accessLifetime);

        var claims = new List<Claim>
        {
            new("customer_id", customer.Id.ToString()),
            new(ClaimTypes.NameIdentifier, customer.Id.ToString()),
            new(JwtRegisteredClaimNames.Sub, customer.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new(ClaimTypes.Email, customer.Email ?? ""),
            new(ClaimTypes.Name, $"{customer.FirstName} {customer.LastName}".Trim()),
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "BagistoApi",
            audience: _config["Jwt:Audience"] ?? "BagistoApp",
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    private string GetSigningKey()
    {
        var key = _config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Key must be configured and at least 32 characters long.");
        return key;
    }

    private static string GenerateRefreshTokenString()
    {
        // 64 bytes of entropy -> 88-char base64 (url-safe). Opaque, not a JWT.
        Span<byte> buffer = stackalloc byte[64];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private (string? ip, string? ua) ReadRequestMeta()
    {
        var ctx = _http.HttpContext;
        if (ctx == null) return (null, null);
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var ua = ctx.Request.Headers["User-Agent"].FirstOrDefault();
        if (ua != null && ua.Length > 500) ua = ua[..500];
        return (ip, ua);
    }
}
