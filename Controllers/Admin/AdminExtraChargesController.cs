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
            c.GroupId,
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

    public record BulkExtraChargeRequest(
        string Name,
        string ChargeType,
        decimal Amount,
        List<int> CategoryIds,
        int SortOrder = 0,
        bool Active = true);

    /// <summary>Add the same extra charge scoped to several categories at once</summary>
    /// <remarks>
    /// Creates one independent charge row per selected category (each can later be edited,
    /// toggled, or deleted on its own) — this is a bulk-create convenience, not a new data
    /// shape. For a single category or a cart-wide charge (no category), use the regular
    /// `POST /api/v1/admin/extra-charges` instead.
    /// </remarks>
    [HttpPost("bulk")]
    public async Task<IActionResult> CreateBulk([FromBody] BulkExtraChargeRequest request)
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

        var categoryIds = (request.CategoryIds ?? new List<int>()).Distinct().ToList();
        if (categoryIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one category." });

        var foundIds = await _db.Categories.Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missing = categoryIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Category id(s) not found: {string.Join(", ", missing)}." });

        var now = DateTime.UtcNow;
        // Shared across every row from this one bulk-add so the admin list
        // can group them back into a single card — see ExtraCharge.GroupId.
        var groupId = Guid.NewGuid().ToString();
        var entities = categoryIds.Select(cid => new ExtraCharge
        {
            Name = name,
            ChargeType = chargeType,
            Amount = request.Amount,
            CategoryId = cid,
            GroupId = groupId,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.ExtraCharges.AddRange(entities);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = $"Added to {entities.Count} categor{(entities.Count == 1 ? "y" : "ies")}.",
            data = await ToDtosAsync(entities)
        });
    }

    public record AddCategoriesToGroupRequest(List<int> CategoryIds);

    /// <summary>Add more categories to an existing bulk-created charge group</summary>
    /// <remarks>
    /// Grows a charge that was originally scoped to several categories (via the `bulk`
    /// endpoint above) with additional ones — new rows copy the group's existing
    /// name/type/amount/sort order/active state. Categories already in this group are
    /// skipped rather than duplicated.
    /// </remarks>
    /// <param name="groupId">The `groupId` shared by the charge's existing rows</param>
    [HttpPost("groups/{groupId}/categories")]
    public async Task<IActionResult> AddCategoriesToGroup(string groupId, [FromBody] AddCategoriesToGroupRequest request)
    {
        if (!await HasPermissionAsync("extra_charges", requireWrite: true)) return AdminForbidden("extra_charges");

        var template = await _db.ExtraCharges
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync();
        if (template == null) return NotFound(new { success = false, message = "Charge group not found." });

        var categoryIds = (request.CategoryIds ?? new List<int>()).Distinct().ToList();
        if (categoryIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one category." });

        var foundIds = await _db.Categories.Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missing = categoryIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Category id(s) not found: {string.Join(", ", missing)}." });

        var existingInGroup = await _db.ExtraCharges.Where(c => c.GroupId == groupId).Select(c => c.CategoryId).ToListAsync();
        var toAdd = categoryIds.Where(cid => !existingInGroup.Contains(cid)).ToList();
        if (toAdd.Count == 0)
            return BadRequest(new { success = false, message = "All selected categories are already part of this charge." });

        var now = DateTime.UtcNow;
        var entities = toAdd.Select(cid => new ExtraCharge
        {
            Name = template.Name,
            ChargeType = template.ChargeType,
            Amount = template.Amount,
            CategoryId = cid,
            GroupId = groupId,
            SortOrder = template.SortOrder,
            IsActive = template.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.ExtraCharges.AddRange(entities);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = $"Added {entities.Count} more categor{(entities.Count == 1 ? "y" : "ies")}.",
            data = await ToDtosAsync(entities)
        });
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
