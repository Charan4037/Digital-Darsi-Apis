using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for the serviceable-pincode allowlist. Orders can only be
/// placed to (and addresses can only be saved for) a pincode present and
/// active in this table — see CheckoutService.PlaceOrderAsync/
/// SaveCheckoutAddressAsync and AccountService.AddOrUpdateAddressAsync for
/// the enforcement side.
/// Routes: /api/v1/admin/service-areas
/// </summary>
[Route("api/v1/admin/service-areas")]
[Tags("Admin – Service Areas")]
public class AdminServiceablePincodesController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminServiceablePincodesController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    /// <summary>List all serviceable pincodes</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!await HasPermissionAsync("service_areas")) return AdminUnauthorized();

        var pincodes = await _db.ServiceablePincodes
            .AsNoTracking()
            .OrderBy(p => p.Town)
            .ToListAsync();
        return Ok(new { success = true, data = pincodes });
    }

    /// <summary>Get a single serviceable pincode by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("service_areas")) return AdminUnauthorized();
        var pincode = await _db.ServiceablePincodes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (pincode == null) return NotFound(new { success = false, message = "Pincode not found." });
        return Ok(new { success = true, data = pincode });
    }

    public record ServiceablePincodeRequest(
        string Pincode,
        string Town,
        string? District,
        string? PostalDivision,
        bool Active = true);

    /// <summary>Add a new serviceable pincode</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ServiceablePincodeRequest request)
    {
        if (!await HasPermissionAsync("service_areas", requireWrite: true)) return AdminForbidden("service_areas");

        var pincode = (request.Pincode ?? "").Trim();
        var town = (request.Town ?? "").Trim();
        if (pincode.Length == 0) return BadRequest(new { success = false, message = "Pincode is required." });
        if (town.Length == 0) return BadRequest(new { success = false, message = "Town is required." });

        if (await _db.ServiceablePincodes.AnyAsync(p => p.Pincode == pincode))
            return BadRequest(new { success = false, message = $"Pincode {pincode} already exists." });

        var now = DateTime.UtcNow;
        var entity = new ServiceablePincode
        {
            Pincode = pincode,
            Town = town,
            District = request.District,
            PostalDivision = request.PostalDivision,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ServiceablePincodes.Add(entity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = entity.Id },
            new { success = true, message = "Pincode added.", data = entity });
    }

    /// <summary>Update a serviceable pincode</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ServiceablePincodeRequest request)
    {
        if (!await HasPermissionAsync("service_areas", requireWrite: true)) return AdminForbidden("service_areas");

        var entity = await _db.ServiceablePincodes.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Pincode not found." });

        var pincode = (request.Pincode ?? "").Trim();
        var town = (request.Town ?? "").Trim();
        if (pincode.Length == 0) return BadRequest(new { success = false, message = "Pincode is required." });
        if (town.Length == 0) return BadRequest(new { success = false, message = "Town is required." });

        if (await _db.ServiceablePincodes.AnyAsync(p => p.Pincode == pincode && p.Id != id))
            return BadRequest(new { success = false, message = $"Pincode {pincode} already exists." });

        entity.Pincode = pincode;
        entity.Town = town;
        entity.District = request.District;
        entity.PostalDivision = request.PostalDivision;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Pincode updated.", data = entity });
    }

    /// <summary>Toggle a serviceable pincode active/inactive without touching its other fields</summary>
    public record ToggleActiveRequest(bool Active);

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> ToggleActive(int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync("service_areas", requireWrite: true)) return AdminForbidden("service_areas");

        var entity = await _db.ServiceablePincodes.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Pincode not found." });

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = entity });
    }

    /// <summary>Delete a serviceable pincode permanently</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync("service_areas", requireWrite: true)) return AdminForbidden("service_areas");

        var entity = await _db.ServiceablePincodes.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Pincode not found." });

        _db.ServiceablePincodes.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Pincode removed." });
    }
}
