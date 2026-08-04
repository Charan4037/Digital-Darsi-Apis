using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for additional cart-wide charges (Handling Charges, Processing
/// Fee, etc.). Every active charge applies to every customer's cart — see
/// ExtraChargeService for the fixed/percentage resolution and
/// CartResourceHelper/CheckoutService for where it's applied.
/// Routes: /api/v1/admin/extra-charges
/// </summary>
[Route("api/v1/admin/extra-charges")]
[Tags("Admin – Extra Charges")]
public class AdminExtraChargesController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private static readonly string[] ValidChargeTypes = { "fixed", "percentage" };

    public AdminExtraChargesController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    /// <summary>List all extra charges</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!await HasPermissionAsync("extra_charges")) return AdminUnauthorized();

        var charges = await _db.ExtraCharges
            .AsNoTracking()
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Id)
            .ToListAsync();
        return Ok(new { success = true, data = await ToDtosAsync(charges) });
    }

    /// <summary>Get a single extra charge by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("extra_charges")) return AdminUnauthorized();
        var charge = await _db.ExtraCharges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (charge == null) return NotFound(new { success = false, message = "Charge not found." });
        return Ok(new { success = true, data = (await ToDtosAsync(new List<ExtraCharge> { charge }))[0] });
    }

    public record ExtraChargeRequest(
        string Name,
        string ChargeType,
        decimal Amount,
        int? CategoryId = null,
        int SortOrder = 0,
        bool Active = true);

    /// <summary>Resolves each charge's category name (when scoped) in one
    /// batched query rather than N+1 lookups.</summary>
    private async Task<List<object>> ToDtosAsync(List<ExtraCharge> charges)
    {
        var categoryIds = charges.Where(c => c.CategoryId != null).Select(c => c.CategoryId!.Value).Distinct().ToList();
        var names = categoryIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Categories
                .Where(c => categoryIds.Contains(c.Id))
                .Select(c => new { c.Id, Name = c.Translations.FirstOrDefault()!.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "");

        return charges.Select(c => (object)new
        {
            c.Id,
            c.Name,
            c.ChargeType,
            c.Amount,
            c.CategoryId,
            CategoryName = c.CategoryId != null && names.TryGetValue(c.CategoryId.Value, out var n) ? n : null,
            c.SortOrder,
            c.IsActive,
            c.CreatedAt,
            c.UpdatedAt
        }).ToList();
    }

    /// <summary>Add a new extra charge</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ExtraChargeRequest request)
    {
        if (!await HasPermissionAsync("extra_charges", requireWrite: true)) return AdminForbidden("extra_charges");

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return BadRequest(new { success = false, message = "Name is required." });
        var chargeType = (request.ChargeType ?? "").Trim().ToLowerInvariant();
        if (!ValidChargeTypes.Contains(chargeType))
            return BadRequest(new { success = false, message = "chargeType must be 'fixed' or 'percentage'." });
        if (request.Amount < 0)
            return BadRequest(new { success = false, message = "Amount cannot be negative." });
        if (chargeType == "percentage" && request.Amount > 100)
            return BadRequest(new { success = false, message = "Percentage cannot exceed 100." });
        if (request.CategoryId.HasValue && !await _db.Categories.AnyAsync(c => c.Id == request.CategoryId.Value))
            return BadRequest(new { success = false, message = "Selected category was not found." });

        var now = DateTime.UtcNow;
        var entity = new ExtraCharge
        {
            Name = name,
            ChargeType = chargeType,
            Amount = request.Amount,
            CategoryId = request.CategoryId,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ExtraCharges.Add(entity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = entity.Id },
            new { success = true, message = "Charge added.", data = (await ToDtosAsync(new List<ExtraCharge> { entity }))[0] });
    }

    /// <summary>Update an extra charge</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ExtraChargeRequest request)
    {
        if (!await HasPermissionAsync("extra_charges", requireWrite: true)) return AdminForbidden("extra_charges");

        var entity = await _db.ExtraCharges.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Charge not found." });

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return BadRequest(new { success = false, message = "Name is required." });
        var chargeType = (request.ChargeType ?? "").Trim().ToLowerInvariant();
        if (!ValidChargeTypes.Contains(chargeType))
            return BadRequest(new { success = false, message = "chargeType must be 'fixed' or 'percentage'." });
        if (request.Amount < 0)
            return BadRequest(new { success = false, message = "Amount cannot be negative." });
        if (chargeType == "percentage" && request.Amount > 100)
            return BadRequest(new { success = false, message = "Percentage cannot exceed 100." });
        if (request.CategoryId.HasValue && !await _db.Categories.AnyAsync(c => c.Id == request.CategoryId.Value))
            return BadRequest(new { success = false, message = "Selected category was not found." });

        entity.Name = name;
        entity.ChargeType = chargeType;
        entity.Amount = request.Amount;
        entity.CategoryId = request.CategoryId;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Charge updated.", data = (await ToDtosAsync(new List<ExtraCharge> { entity }))[0] });
    }

    /// <summary>Toggle an extra charge active/inactive without touching its other fields</summary>
    public record ToggleActiveRequest(bool Active);

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> ToggleActive(int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync("extra_charges", requireWrite: true)) return AdminForbidden("extra_charges");

        var entity = await _db.ExtraCharges.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Charge not found." });

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = (await ToDtosAsync(new List<ExtraCharge> { entity }))[0] });
    }

    /// <summary>Delete an extra charge permanently</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync("extra_charges", requireWrite: true)) return AdminForbidden("extra_charges");

        var entity = await _db.ExtraCharges.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Charge not found." });

        _db.ExtraCharges.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Charge removed." });
    }
}
