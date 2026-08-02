using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Services;

/// <summary>A single resolved charge line, ready to display or persist —
/// Amount is always the actual rupee value, whether the charge itself is
/// "fixed" or "percentage" of the cart subtotal.</summary>
public record ExtraChargeLine(string Name, string ChargeType, decimal Rate, decimal Amount);

/// <summary>
/// Resolves the admin-managed extra-charge list (Handling Charges,
/// Processing Fee, etc. — see ExtraCharge / AdminExtraChargesController)
/// against a cart's subtotal. Every active charge applies to every cart;
/// there's no per-vendor/per-product targeting.
/// </summary>
public class ExtraChargeService
{
    private readonly DOSDbContext _db;

    public ExtraChargeService(DOSDbContext db)
    {
        _db = db;
    }

    public async Task<(List<ExtraChargeLine> lines, decimal total)> ComputeAsync(decimal subtotal)
    {
        var charges = await _db.ExtraCharges
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Id)
            .ToListAsync();

        var lines = new List<ExtraChargeLine>();
        decimal total = 0;
        foreach (var c in charges)
        {
            var amount = c.ChargeType == "percentage"
                ? Math.Round(subtotal * c.Amount / 100m, 2, MidpointRounding.AwayFromZero)
                : c.Amount;
            lines.Add(new ExtraChargeLine(c.Name, c.ChargeType, c.Amount, amount));
            total += amount;
        }
        return (lines, total);
    }
}
