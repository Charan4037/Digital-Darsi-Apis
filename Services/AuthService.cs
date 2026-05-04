using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using BagistoApi.Data;
using BagistoApi.Models.Customer;

namespace BagistoApi.Services;

public class AuthService
{
    private readonly BagistoDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor _http;

    public AuthService(BagistoDbContext db, IConfiguration config, IHttpContextAccessor http)
    {
        _db = db;
        _config = config;
        _http = http;
    }

    public async Task<(Customer? customer, string? token, string message, bool success)> LoginAsync(string email, string password)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == email && c.Status == 1);
        if (customer == null)
            return (null, null, "Invalid email or password.", false);

        if (string.IsNullOrEmpty(customer.Password))
            return (null, null, "Invalid email or password.", false);

        // Laravel bcrypt passwords start with $2y$ — BCrypt.Net handles $2a$/$2b$/$2y$
        var pwd = customer.Password.Replace("$2y$", "$2a$");
        if (!BCrypt.Net.BCrypt.Verify(password, pwd))
            return (null, null, "Invalid email or password.", false);

        var token = GenerateJwt(customer);
        return (customer, token, "Login successful.", true);
    }

    public async Task<(Customer? customer, string? token, string message, bool success)> RegisterAsync(
        string firstName, string lastName, string email, string password)
    {
        if (await _db.Customers.AnyAsync(c => c.Email == email))
            return (null, null, "Email already exists.", false);

        var customer = new Customer
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            Password = BCrypt.Net.BCrypt.HashPassword(password),
            Status = 1,
            IsVerified = true,
            ChannelId = 1,
            CustomerGroupId = 2, // General group
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var token = GenerateJwt(customer);
        return (customer, token, "Registration successful.", true);
    }

    public async Task<(string message, bool success)> ForgotPasswordAsync(string email)
    {
        var exists = await _db.Customers.AnyAsync(c => c.Email == email);
        if (!exists)
            return ("We cannot find a user with that email address.", false);

        return ("We have e-mailed your password reset link!", true);
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

    private string GenerateJwt(Customer customer)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            _config["Jwt:Key"] ?? "BagistoApiSecretKey2024VeryLongKeyForSecurity!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("customer_id", customer.Id.ToString()),
            new Claim(ClaimTypes.Email, customer.Email ?? ""),
            new Claim(ClaimTypes.Name, $"{customer.FirstName} {customer.LastName}")
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "BagistoApi",
            audience: _config["Jwt:Audience"] ?? "BagistoApp",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
