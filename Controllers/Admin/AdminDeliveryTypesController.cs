using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for delivery/shipping tiers (Express, Normal, Free, or
/// whatever the admin configures — previously a fixed 3-row seed with no
/// admin surface at all) and their optional per-category price overrides.
/// See DeliveryChargeService for how a cart's price resolves — a tier with
/// no overrides always charges its global default; with overrides, the
/// highest applicable rate across the cart's categories wins.
/// Routes: /api/v1/admin/delivery-types
/// </summary>
[Route("api/v1/admin/delivery-types")]
[Tags("Admin – Delivery Types")]
public class AdminDeliveryTypesController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private const string Resource = "delivery_types";

    public AdminDeliveryTypesController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    // ─── Tiers ──────────────────────────────────────────────────────────

    public record DeliveryTypeRequest(
        string Code,
        string Name,
        string? Description,
        decimal Price,
        int DeliveryHours,
        int SortOrder = 0,
        bool Active = true);

    /// <summary>List all delivery tiers, each with its category price overrides.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();

        var tiers = await _db.DeliveryTypes
            .AsNoTracking()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Id)
            .ToListAsync();
        return Ok(new { success = true, data = await ToDtosAsync(tiers) });
    }

    /// <summary>Get a single delivery tier by ID.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();
        var tier = await _db.DeliveryTypes.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (tier == null) return NotFound(new { success = false, message = "Delivery type not found." });
        return Ok(new { success = true, data = (await ToDtosAsync(new List<DeliveryType> { tier }))[0] });
    }

    /// <summary>Add a new delivery tier.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeliveryTypeRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var (ok, error, code, name) = ValidateRequest(request);
        if (!ok) return BadRequest(new { success = false, message = error });
        if (await _db.DeliveryTypes.AnyAsync(d => d.Code == code))
            return BadRequest(new { success = false, message = "A delivery type with this code already exists." });

        var now = DateTime.UtcNow;
        var entity = new DeliveryType
        {
            Code = code,
            Name = name,
            Description = request.Description,
            Price = request.Price,
            DeliveryHours = request.DeliveryHours,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.DeliveryTypes.Add(entity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = entity.Id },
            new { success = true, message = "Delivery type added.", data = (await ToDtosAsync(new List<DeliveryType> { entity }))[0] });
    }

    /// <summary>Update a delivery tier.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DeliveryTypeRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.DeliveryTypes.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Delivery type not found." });

        var (ok, error, code, name) = ValidateRequest(request);
        if (!ok) return BadRequest(new { success = false, message = error });
        if (await _db.DeliveryTypes.AnyAsync(d => d.Code == code && d.Id != id))
            return BadRequest(new { success = false, message = "A delivery type with this code already exists." });

        entity.Code = code;
        entity.Name = name;
        entity.Description = request.Description;
        entity.Price = request.Price;
        entity.DeliveryHours = request.DeliveryHours;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Delivery type updated.", data = (await ToDtosAsync(new List<DeliveryType> { entity }))[0] });
    }

    /// <summary>Toggle a delivery tier active/inactive — lets the admin keep
    /// just 1 option (or any subset) selectable at checkout without deleting
    /// the rest.</summary>
    public record ToggleActiveRequest(bool Active);

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> ToggleActive(int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.DeliveryTypes.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Delivery type not found." });

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = (await ToDtosAsync(new List<DeliveryType> { entity }))[0] });
    }

    /// <summary>Delete a delivery tier permanently, along with its category price overrides.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.DeliveryTypes.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Delivery type not found." });

        var overrides = await _db.DeliveryTypeCategoryPrices.Where(o => o.DeliveryTypeId == id).ToListAsync();
        _db.DeliveryTypeCategoryPrices.RemoveRange(overrides);
        _db.DeliveryTypes.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Delivery type removed." });
    }

    // ─── Category price overrides ──────────────────────────────────────

    public record CategoryPriceRequest(int CategoryId, decimal Price, bool Active = true);

    /// <summary>List a tier's category price overrides.</summary>
    [HttpGet("{deliveryTypeId:int}/category-prices")]
    public async Task<IActionResult> ListCategoryPrices(int deliveryTypeId)
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();
        if (!await _db.DeliveryTypes.AnyAsync(d => d.Id == deliveryTypeId))
            return NotFound(new { success = false, message = "Delivery type not found." });

        var overrides = await _db.DeliveryTypeCategoryPrices
            .AsNoTracking()
            .Where(o => o.DeliveryTypeId == deliveryTypeId)
            .OrderBy(o => o.Id)
            .ToListAsync();
        return Ok(new { success = true, data = await ToOverrideDtosAsync(overrides) });
    }

    /// <summary>Add a category price override for a tier.</summary>
    [HttpPost("{deliveryTypeId:int}/category-prices")]
    public async Task<IActionResult> CreateCategoryPrice(int deliveryTypeId, [FromBody] CategoryPriceRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);
        if (!await _db.DeliveryTypes.AnyAsync(d => d.Id == deliveryTypeId))
            return NotFound(new { success = false, message = "Delivery type not found." });
        if (request.Price < 0)
            return BadRequest(new { success = false, message = "Price cannot be negative." });
        if (!await _db.Categories.AnyAsync(c => c.Id == request.CategoryId))
            return BadRequest(new { success = false, message = "Selected category was not found." });
        if (await _db.DeliveryTypeCategoryPrices.AnyAsync(o => o.DeliveryTypeId == deliveryTypeId && o.CategoryId == request.CategoryId))
            return BadRequest(new { success = false, message = "This category already has a price override for this delivery type." });

        var now = DateTime.UtcNow;
        var entity = new DeliveryTypeCategoryPrice
        {
            DeliveryTypeId = deliveryTypeId,
            CategoryId = request.CategoryId,
            Price = request.Price,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.DeliveryTypeCategoryPrices.Add(entity);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Category price added.", data = (await ToOverrideDtosAsync(new List<DeliveryTypeCategoryPrice> { entity }))[0] });
    }

    /// <summary>Update a category price override.</summary>
    [HttpPut("{deliveryTypeId:int}/category-prices/{id:int}")]
    public async Task<IActionResult> UpdateCategoryPrice(int deliveryTypeId, int id, [FromBody] CategoryPriceRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.DeliveryTypeCategoryPrices.FirstOrDefaultAsync(o => o.Id == id && o.DeliveryTypeId == deliveryTypeId);
        if (entity == null) return NotFound(new { success = false, message = "Category price override not found." });
        if (request.Price < 0)
            return BadRequest(new { success = false, message = "Price cannot be negative." });
        if (!await _db.Categories.AnyAsync(c => c.Id == request.CategoryId))
            return BadRequest(new { success = false, message = "Selected category was not found." });
        if (await _db.DeliveryTypeCategoryPrices.AnyAsync(o => o.DeliveryTypeId == deliveryTypeId && o.CategoryId == request.CategoryId && o.Id != id))
            return BadRequest(new { success = false, message = "This category already has a price override for this delivery type." });

        entity.CategoryId = request.CategoryId;
        entity.Price = request.Price;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Category price updated.", data = (await ToOverrideDtosAsync(new List<DeliveryTypeCategoryPrice> { entity }))[0] });
    }

    /// <summary>Toggle a category price override active/inactive.</summary>
    [HttpPatch("{deliveryTypeId:int}/category-prices/{id:int}/status")]
    public async Task<IActionResult> ToggleCategoryPriceActive(int deliveryTypeId, int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.DeliveryTypeCategoryPrices.FirstOrDefaultAsync(o => o.Id == id && o.DeliveryTypeId == deliveryTypeId);
        if (entity == null) return NotFound(new { success = false, message = "Category price override not found." });

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = (await ToOverrideDtosAsync(new List<DeliveryTypeCategoryPrice> { entity }))[0] });
    }

    /// <summary>Delete a category price override.</summary>
    [HttpDelete("{deliveryTypeId:int}/category-prices/{id:int}")]
    public async Task<IActionResult> DeleteCategoryPrice(int deliveryTypeId, int id)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.DeliveryTypeCategoryPrices.FirstOrDefaultAsync(o => o.Id == id && o.DeliveryTypeId == deliveryTypeId);
        if (entity == null) return NotFound(new { success = false, message = "Category price override not found." });

        _db.DeliveryTypeCategoryPrices.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Category price override removed." });
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static (bool ok, string? error, string code, string name) ValidateRequest(DeliveryTypeRequest request)
    {
        var code = (request.Code ?? "").Trim().ToLowerInvariant();
        var name = (request.Name ?? "").Trim();
        if (code.Length == 0) return (false, "Code is required.", code, name);
        if (name.Length == 0) return (false, "Name is required.", code, name);
        if (request.Price < 0) return (false, "Price cannot be negative.", code, name);
        if (request.DeliveryHours <= 0) return (false, "Delivery hours must be greater than 0.", code, name);
        return (true, null, code, name);
    }

    /// <summary>Resolves each tier's category overrides (with category names,
    /// batched) in one pass rather than N+1 lookups.</summary>
    private async Task<List<object>> ToDtosAsync(List<DeliveryType> tiers)
    {
        var tierIds = tiers.Select(t => t.Id).ToList();
        var overrides = await _db.DeliveryTypeCategoryPrices
            .AsNoTracking()
            .Where(o => tierIds.Contains(o.DeliveryTypeId))
            .OrderBy(o => o.Id)
            .ToListAsync();
        var overridesByTier = new Dictionary<int, List<object>>();
        foreach (var group in overrides.GroupBy(o => o.DeliveryTypeId))
            overridesByTier[group.Key] = await ToOverrideDtosAsync(group.ToList());

        return tiers.Select(t => (object)new
        {
            t.Id,
            t.Code,
            t.Name,
            t.Description,
            t.Price,
            t.DeliveryHours,
            t.SortOrder,
            t.IsActive,
            t.CreatedAt,
            t.UpdatedAt,
            CategoryPrices = overridesByTier.TryGetValue(t.Id, out var list) ? list : new List<object>()
        }).ToList();
    }

    private async Task<List<object>> ToOverrideDtosAsync(List<DeliveryTypeCategoryPrice> overrides)
    {
        var categoryIds = overrides.Select(o => o.CategoryId).Distinct().ToList();
        var names = categoryIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Categories
                .Where(c => categoryIds.Contains(c.Id))
                .Select(c => new { c.Id, Name = c.Translations.FirstOrDefault()!.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "");

        return overrides.Select(o => (object)new
        {
            o.Id,
            o.DeliveryTypeId,
            o.CategoryId,
            CategoryName = names.TryGetValue(o.CategoryId, out var n) ? n : null,
            o.Price,
            o.IsActive,
            o.CreatedAt,
            o.UpdatedAt
        }).ToList();
    }
}
