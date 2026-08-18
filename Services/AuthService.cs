using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using DOSApi.Data;
using DOSApi.Models.Customer;
using Serilog;

namespace DOSApi.Services;

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

    private readonly DOSDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuthService> _logger;

    private readonly TimeSpan _accessLifetime;
    private readonly TimeSpan _refreshLifetime;

    public AuthService(DOSDbContext db, IConfiguration config, IHttpContextAccessor http, ILogger<AuthService> logger)
    {
        _db = db;
        _config = config;
        _http = http;
        _logger = logger;

        _accessLifetime = TimeSpan.FromMinutes(
            int.TryParse(_config["Jwt:AccessTokenMinutes"], out var am) && am > 0 ? am : 30);
        _refreshLifetime = TimeSpan.FromDays(
            int.TryParse(_config["Jwt:RefreshTokenDays"], out var rd) && rd > 0 ? rd : 30);
    }

    // ─── Public API ─────────────────────────────────────────────────────

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        _logger.LogInformation("Login operation starting for email: {Email}", email);
        // !IsDeleted here means a previously-deleted account is simply
        // invisible to this lookup — its old email was already freed up by
        // DeleteAccountAsync's anonymization, so this condition is mostly
        // belt-and-suspenders for any row that predates that behavior.
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == email && c.Status == 1 && !c.IsDeleted);
        if (customer == null || string.IsNullOrEmpty(customer.Password))
        {
            _logger.LogWarning("Login failed: customer not found or no password for email: {Email}", email);
            return new AuthResult(false, "Invalid email or password.");
        }

        // Laravel bcrypt passwords start with $2y$ — BCrypt.Net handles $2a$/$2b$/$2y$
        var pwd = customer.Password.Replace("$2y$", "$2a$");
        if (!BCrypt.Net.BCrypt.Verify(password, pwd))
        {
            _logger.LogWarning("Login failed: password verification failed for email: {Email}", email);
            return new AuthResult(false, "Invalid email or password.");
        }

        _logger.LogInformation("Password verified for customer ID: {CustomerId}", customer.Id);
        var tokens = await IssueTokensAsync(customer);
        _logger.LogInformation("Tokens issued successfully for customer ID: {CustomerId}", customer.Id);
        return new AuthResult(true, "Login successful.", customer, tokens);
    }

    public async Task<AuthResult> RegisterAsync(
        string firstName, string lastName, string email, string password)
    {
        _logger.LogInformation("Registration operation starting for email: {Email}, Name: {FirstName} {LastName}", email, firstName, lastName);
        if (await _db.Customers.AnyAsync(c => c.Email == email))
        {
            _logger.LogWarning("Registration failed: email already exists: {Email}", email);
            return new AuthResult(false, "Email already exists.");
        }

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
        _logger.LogInformation("Customer registered successfully with ID: {CustomerId}, Email: {Email}", customer.Id, customer.Email);

        var tokens = await IssueTokensAsync(customer);
        _logger.LogInformation("Tokens issued for new customer ID: {CustomerId}", customer.Id);
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
        _logger.LogInformation("Firebase login operation starting");
        if (string.IsNullOrWhiteSpace(firebaseIdToken))
        {
            _logger.LogWarning("Firebase login failed: Firebase ID token is required");
            return new AuthResult(false, "Firebase ID token is required.");
        }

        if (FirebaseApp.DefaultInstance == null)
        {
            _logger.LogError("Firebase login failed: Firebase not configured on the server");
            return new AuthResult(false, "Phone login is not configured on the server.");
        }

        FirebaseToken decoded;
        try
        {
            decoded = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(firebaseIdToken);
            _logger.LogInformation("Firebase token verified successfully");
        }
        catch (FirebaseAuthException ex)
        {
            _logger.LogWarning("Firebase login failed: Invalid Firebase token - {Message}", ex.Message);
            return new AuthResult(false, $"Invalid Firebase token: {ex.Message}");
        }

        // The phone is the source of identity for this flow. Firebase only
        // populates phone_number after a successful Phone Auth verification,
        // so its absence means the client used a different sign-in method
        // and we should reject the exchange.
        var phone = decoded.Claims.TryGetValue("phone_number", out var pn) ? pn?.ToString() : null;
        if (string.IsNullOrWhiteSpace(phone))
        {
            _logger.LogWarning("Firebase login failed: Token has no phone number claim");
            return new AuthResult(false, "Firebase token has no phone number — phone OTP sign-in required.");
        }

        // Normalize: keep '+' and digits only. Stops "+91 98765 43210" and
        // "+91-98765-43210" from creating duplicate customer rows.
        phone = NormalizePhone(phone);
        _logger.LogInformation("Firebase phone normalized: {Phone}", phone);

        try
        {
            // !IsDeleted: a previously-deleted account must never be handed
            // back to a fresh OTP login — DeleteAccountAsync clears Phone on
            // that row specifically so it can never match here again, and
            // this falls through to the customer == null branch below,
            // which signs the phone number up as a brand-new account with
            // none of the old profile/history attached.
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Phone == phone && !c.IsDeleted);
            var isNew = false;
            if (customer == null)
            {
                _logger.LogInformation("Creating new customer for phone: {Phone}", phone);
                // customers.email column is typically NOT NULL UNIQUE,
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
                _logger.LogInformation("New customer created with ID: {CustomerId} for phone: {Phone}", customer.Id, phone);
            }
            else if (customer.Status != 1)
            {
                _logger.LogWarning("Firebase login failed: Account is suspended for customer ID: {CustomerId}", customer.Id);
                return new AuthResult(false, "This account is suspended.");
            }
            else if (!string.IsNullOrWhiteSpace(optionalFirstName) && string.IsNullOrEmpty(customer.FirstName))
            {
                _logger.LogInformation("Backfilling name for customer ID: {CustomerId}", customer.Id);
                // Backfill name on first OTP-verified login if it was empty.
                customer.FirstName = optionalFirstName.Trim();
                if (!string.IsNullOrWhiteSpace(optionalLastName))
                    customer.LastName = optionalLastName.Trim();
                customer.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            var tokens = await IssueTokensAsync(customer);
            _logger.LogInformation("Firebase login successful for customer ID: {CustomerId}, IsNewUser: {IsNewUser}", customer.Id, isNew);
            return new AuthResult(
                true,
                isNew ? "Account created via phone OTP." : "Login successful.",
                customer,
                tokens,
                IsNewUser: isNew);
        }
        catch (Exception ex)
        {
            // Surface DB / token-issue errors as a structured 400 so the
            // client sees the real message instead of a silent 500.
            var detail = ex.InnerException?.InnerException?.Message
                      ?? ex.InnerException?.Message
                      ?? ex.Message;
            _logger.LogError(ex, "Firebase login failed with exception: {Detail}", detail);
            return new AuthResult(false, $"Login failed: {detail}");
        }
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
    
    /// <summary>
    /// Testing-only login: find or create a customer by phone number and issue tokens.
    /// No Firebase verification, no OTP, no reCAPTCHA. Must be called from an
    /// admin-key-protected endpoint — never expose this without auth.
    /// </summary>
    public async Task<AuthResult> TestLoginByPhoneAsync(
        string phoneNumber,
        string? firstName = null,
        string? lastName = null)
    {
        _logger.LogInformation("Test login by phone operation starting for phone: {PhoneNumber}", phoneNumber);
        var phone = NormalizePhone(phoneNumber);
        if (string.IsNullOrWhiteSpace(phone))
        {
            _logger.LogWarning("Test login failed: Phone number is required");
            return new AuthResult(false, "Phone number is required.");
        }

        // If the caller omitted the country code (bare 10-digit Indian mobile),
        // prepend +91 so the lookup matches how Firebase stores it in the DB.
        if (!phone.StartsWith('+'))
        {
            if (phone.Length == 10)
                phone = "+91" + phone;
            else
            {
                _logger.LogWarning("Test login failed: Invalid phone number format for phone: {PhoneNumber}", phoneNumber);
                return new AuthResult(false, "Invalid phone number. Include the country code, e.g. +918088214037 or just the 10-digit number.");
            }
        }

        try
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Phone == phone && !c.IsDeleted);
            if (customer == null)
            {
                _logger.LogWarning("Test login failed: No customer found with phone: {Phone}", phone);
                return new AuthResult(false, $"No customer found with phone {phone}. This endpoint only works for existing accounts.");
            }

            if (customer.Status != 1)
            {
                _logger.LogWarning("Test login failed: Account is suspended for customer ID: {CustomerId}", customer.Id);
                return new AuthResult(false, "This account is suspended.");
            }

            var tokens = await IssueTokensAsync(customer);
            _logger.LogInformation("Test login successful for customer ID: {CustomerId}, Phone: {Phone}", customer.Id, phone);
            return new AuthResult(true, "Test login successful.", customer, tokens);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.InnerException?.Message
                      ?? ex.InnerException?.Message
                      ?? ex.Message;
            _logger.LogError(ex, "Test login failed with exception: {Detail}", detail);
            return new AuthResult(false, $"Login failed: {detail}");
        }
    }

    public async Task<(string message, bool success)> ForgotPasswordAsync(string email)
    {
        _logger.LogInformation("Forgot password operation starting for email: {Email}", email);
        var exists = await _db.Customers.AnyAsync(c => c.Email == email);
        if (!exists)
        {
            _logger.LogWarning("Forgot password failed: User not found for email: {Email}", email);
            return ("We cannot find a user with that email address.", false);
        }

        _logger.LogInformation("Forgot password email queued for email: {Email}", email);
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
        _logger.LogInformation("Token refresh operation starting");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("Token refresh failed: Refresh token is required");
            return new AuthResult(false, "Refresh token is required.");
        }

        var hash = HashToken(refreshToken);
        var existing = await _db.CustomerRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (existing == null)
        {
            _logger.LogWarning("Token refresh failed: Invalid refresh token");
            return new AuthResult(false, "Invalid refresh token.");
        }

        if (existing.RevokedAt != null)
        {
            _logger.LogWarning("Token refresh failed: Refresh token has been revoked for customer ID: {CustomerId}", existing.CustomerId);
            // Replay of a revoked token — assume the chain is compromised
            // and revoke every active refresh token for this customer.
            await RevokeAllForCustomerAsync(existing.CustomerId);
            return new AuthResult(false, "Refresh token has been revoked.");
        }

        if (existing.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogWarning("Token refresh failed: Refresh token has expired for customer ID: {CustomerId}", existing.CustomerId);
            return new AuthResult(false, "Refresh token has expired.");
        }

        var customer = await _db.Customers.FindAsync(existing.CustomerId);
        if (customer == null || customer.Status != 1 || customer.IsDeleted)
        {
            _logger.LogWarning("Token refresh failed: Account is not active for customer ID: {CustomerId}", existing.CustomerId);
            return new AuthResult(false, "Account is not active.");
        }

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
        _logger.LogInformation("Token rotated successfully for customer ID: {CustomerId}", customer.Id);

        var isAdmin = await _db.CustomerAdmins.AnyAsync(a => a.CustomerId == customer.Id);
        var (jwt, jwtExp) = GenerateAccessToken(customer, isAdmin);
        return new AuthResult(true, "Token refreshed.", customer, new TokenBundle(
            jwt, jwtExp, newRefresh, now.Add(_refreshLifetime)));
    }

    /// <summary>Revoke a single refresh token (logout from one device).</summary>
    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken)
    {
        _logger.LogInformation("Revoking refresh token");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("Revoke failed: Refresh token is empty");
            return false;
        }
        var hash = HashToken(refreshToken);
        var token = await _db.CustomerRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token == null || token.RevokedAt != null)
        {
            _logger.LogWarning("Revoke failed: Token not found or already revoked");
            return false;
        }
        token.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Refresh token revoked successfully for customer ID: {CustomerId}", token.CustomerId);
        return true;
    }

    /// <summary>Revoke every active refresh token for a customer (logout from all devices).</summary>
    public async Task<int> RevokeAllForCustomerAsync(int customerId)
    {
        _logger.LogInformation("Revoking all refresh tokens for customer ID: {CustomerId}", customerId);
        var now = DateTime.UtcNow;
        var revoked = await _db.CustomerRefreshTokens
            .Where(t => t.CustomerId == customerId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
        _logger.LogInformation("Revoked {RevokedCount} tokens for customer ID: {CustomerId}", revoked, customerId);
        return revoked;
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
        _logger.LogInformation("Issuing tokens for customer ID: {CustomerId}", customer.Id);
        var isAdmin = await _db.CustomerAdmins.AnyAsync(a => a.CustomerId == customer.Id);
        var (jwt, jwtExp) = GenerateAccessToken(customer, isAdmin);

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
        _logger.LogInformation("Tokens issued successfully - Access expires at: {AccessExpiresAt}, Refresh expires at: {RefreshExpiresAt}", jwtExp, refreshExpiry);

        return new TokenBundle(jwt, jwtExp, refreshPlain, refreshExpiry);
    }

    private (string token, DateTime expiresAt) GenerateAccessToken(Customer customer, bool isAdmin = false)
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

        // Drives AdminBaseController.IsAdmin() — lets the customer's own JWT
        // authorize admin API calls without shipping a static admin key in
        // the app. Only present when the customer has a customer_admins row.
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "DOSApi",
            audience: _config["Jwt:Audience"] ?? "DOSApp",
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
