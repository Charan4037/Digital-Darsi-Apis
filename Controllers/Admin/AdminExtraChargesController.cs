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
        return Ok(new { success = true, data = charges });
    }

    /// <summary>Get a single extra charge by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("extra_charges")) return AdminUnauthorized();
        var charge = await _db.ExtraCharges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (charge == null) return NotFound(new { success = false, message = "Charge not found." });
        return Ok(new { success = true, data = charge });
    }

    public record ExtraChargeRequest(
        string Name,
        string ChargeType,
        decimal Amount,
        int SortOrder = 0,
        bool Active = true);

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

        var now = DateTime.UtcNow;
        var entity = new ExtraCharge
        {
            Name = name,
            ChargeType = chargeType,
            Amount = request.Amount,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ExtraCharges.Add(entity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = entity.Id },
            new { success = true, message = "Charge added.", data = entity });
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

        entity.Name = name;
        entity.ChargeType = chargeType;
        entity.Amount = request.Amount;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Charge updated.", data = entity });
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

        return Ok(new { success = true, message = "Status updated.", data = entity });
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
