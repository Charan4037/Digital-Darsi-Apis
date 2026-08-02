using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Services;

/// <summary>
/// Enforces the serviceable-pincode allowlist (see ServiceablePincode /
/// AdminServiceablePincodesController) against customer-supplied postcodes.
/// Shared by CheckoutService (checkout address + order placement) and
/// AccountService (address book) so the rule lives in exactly one place.
/// </summary>
public class ServiceAreaService
{
    private readonly DOSDbContext _db;

    public ServiceAreaService(DOSDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsPincodeServiceableAsync(string? postcode)
    {
        var trimmed = postcode?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        return await _db.ServiceablePincodes
            .AnyAsync(p => p.Pincode == trimmed && p.IsActive);
    }

    /// <summary>
    /// Standard rejection message for an out-of-area postcode, listing the
    /// towns we currently deliver to so the customer isn't left guessing.
    /// </summary>
    public async Task<string> BuildUnserviceableMessageAsync()
    {
        var towns = await _db.ServiceablePincodes
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Town)
            .Select(p => p.Town)
            .ToListAsync();

        return towns.Count > 0
            ? $"Sorry, we don't deliver to this pincode yet. We currently deliver to: {string.Join(", ", towns)}."
            : "Sorry, we don't deliver to this pincode yet.";
    }
}
