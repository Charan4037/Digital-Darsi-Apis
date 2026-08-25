using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for preorder targeting rules (which products/vendors/
/// categories/locations are currently under preorder) and each rule's own
/// delivery time slots. See PreorderService.ResolveAsync for how overlapping
/// rules resolve (most specific scope wins: Product > Vendor > Category >
/// Location > Global).
/// This is the admin-configuration half of the feature only — no cart/
/// checkout wiring yet, see the `resolve` endpoint below for a way to
/// preview what a given product/pincode would resolve to.
/// Routes: /api/v1/admin/preorder
/// </summary>
[Route("api/v1/admin/preorder")]
[Tags("Admin – Preorder")]
public class AdminPreorderController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly PreorderService _preorderService;
    private const string Resource = "preorder";
    private static readonly HashSet<string> ValidScopeTypes = new() { "global", "location", "category", "vendor", "product" };

    public AdminPreorderController(DOSDbContext db, IConfiguration config, PreorderService preorderService) : base(db, config)
    {
        _db = db;
        _preorderService = preorderService;
    }

    // ─── Rules ──────────────────────────────────────────────────────────

    public record PreorderRuleRequest(
        string ScopeType,
        int? ProductId,
        int? VendorId,
        int? CategoryId,
        string? Pincode,
        int? WindowDays,
        int? MinLeadHours,
        string? Note,
        bool Active = true);

    /// <summary>List all preorder rules, each with its time slots. Optionally filter by scope type.</summary>
    [HttpGet("rules")]
    public async Task<IActionResult> List([FromQuery] string? scopeType)
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();

        var query = _db.PreorderRules.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(scopeType))
            query = query.Where(r => r.ScopeType == scopeType);

        var rules = await query.OrderByDescending(r => r.Id).ToListAsync();
        return Ok(new { success = true, data = await ToDtosAsync(rules) });
    }

    /// <summary>Get a single preorder rule by ID.</summary>
    [HttpGet("rules/{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();
        var rule = await _db.PreorderRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (rule == null) return NotFound(new { success = false, message = "Preorder rule not found." });
        return Ok(new { success = true, data = (await ToDtosAsync(new List<PreorderRule> { rule }))[0] });
    }

    /// <summary>Add a new preorder rule.</summary>
    [HttpPost("rules")]
    public async Task<IActionResult> Create([FromBody] PreorderRuleRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var (ok, error, scopeType) = await ValidateAsync(request, excludeId: null);
        if (!ok) return BadRequest(new { success = false, message = error });

        var now = DateTime.UtcNow;
        var entity = new PreorderRule
        {
            ScopeType = scopeType,
            ProductId = request.ProductId,
            VendorId = request.VendorId,
            CategoryId = request.CategoryId,
            Pincode = string.IsNullOrWhiteSpace(request.Pincode) ? null : request.Pincode.Trim(),
            IsActive = request.Active,
            WindowDays = request.WindowDays,
            MinLeadHours = request.MinLeadHours,
            Note = request.Note,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.PreorderRules.Add(entity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = entity.Id },
            new { success = true, message = "Preorder rule added.", data = (await ToDtosAsync(new List<PreorderRule> { entity }))[0] });
    }

    /// <summary>Update a preorder rule.</summary>
    [HttpPut("rules/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] PreorderRuleRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.PreorderRules.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Preorder rule not found." });

        var (ok, error, scopeType) = await ValidateAsync(request, excludeId: id);
        if (!ok) return BadRequest(new { success = false, message = error });

        entity.ScopeType = scopeType;
        entity.ProductId = request.ProductId;
        entity.VendorId = request.VendorId;
        entity.CategoryId = request.CategoryId;
        entity.Pincode = string.IsNullOrWhiteSpace(request.Pincode) ? null : request.Pincode.Trim();
        entity.IsActive = request.Active;
        entity.WindowDays = request.WindowDays;
        entity.MinLeadHours = request.MinLeadHours;
        entity.Note = request.Note;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Preorder rule updated.", data = (await ToDtosAsync(new List<PreorderRule> { entity }))[0] });
    }

    public record ToggleActiveRequest(bool Active);

    /// <summary>Toggle a preorder rule active/inactive without touching its other fields.</summary>
    [HttpPatch("rules/{id:int}/status")]
    public async Task<IActionResult> ToggleActive(int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.PreorderRules.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Preorder rule not found." });

        if (request.Active)
        {
            var (ok, error) = await CheckNoDuplicateActiveAsync(entity.ScopeType, entity.ProductId, entity.VendorId, entity.CategoryId, entity.Pincode, excludeId: id);
            if (!ok) return BadRequest(new { success = false, message = error });
        }

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = (await ToDtosAsync(new List<PreorderRule> { entity }))[0] });
    }

    /// <summary>Delete a preorder rule permanently, along with its time slots
    /// (the DB's own ON DELETE CASCADE on preorder_slots.preorder_rule_id —
    /// see PreorderSeeder — handles the slots; EF must NOT also issue a
    /// separate DELETE for them, or it finds 0 rows already gone once MySQL's
    /// cascade beats it to the parent row and throws a spurious
    /// DbUpdateConcurrencyException).</summary>
    [HttpDelete("rules/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.PreorderRules.FindAsync(id);
        if (entity == null) return NotFound(new { success = false, message = "Preorder rule not found." });

        _db.PreorderRules.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Preorder rule removed." });
    }

    // ─── Slots (owned by a rule) ────────────────────────────────────────

    public record PreorderSlotRequest(string Label, string StartTime, string EndTime, int SortOrder = 0, bool Active = true);

    /// <summary>List a rule's time slots.</summary>
    [HttpGet("rules/{ruleId:int}/slots")]
    public async Task<IActionResult> ListSlots(int ruleId)
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();
        if (!await _db.PreorderRules.AnyAsync(r => r.Id == ruleId))
            return NotFound(new { success = false, message = "Preorder rule not found." });

        var slots = await _db.PreorderSlots
            .AsNoTracking()
            .Where(s => s.PreorderRuleId == ruleId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .ToListAsync();
        return Ok(new { success = true, data = slots.Select(ToSlotDto) });
    }

    /// <summary>Add a time slot to a rule.</summary>
    [HttpPost("rules/{ruleId:int}/slots")]
    public async Task<IActionResult> CreateSlot(int ruleId, [FromBody] PreorderSlotRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);
        if (!await _db.PreorderRules.AnyAsync(r => r.Id == ruleId))
            return NotFound(new { success = false, message = "Preorder rule not found." });

        var (ok, error, start, end, label) = ValidateSlot(request);
        if (!ok) return BadRequest(new { success = false, message = error });

        var now = DateTime.UtcNow;
        var entity = new PreorderSlot
        {
            PreorderRuleId = ruleId,
            Label = label,
            StartTime = start,
            EndTime = end,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.PreorderSlots.Add(entity);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Time slot added.", data = ToSlotDto(entity) });
    }

    /// <summary>Update a rule's time slot.</summary>
    [HttpPut("rules/{ruleId:int}/slots/{id:int}")]
    public async Task<IActionResult> UpdateSlot(int ruleId, int id, [FromBody] PreorderSlotRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.PreorderSlots.FirstOrDefaultAsync(s => s.Id == id && s.PreorderRuleId == ruleId);
        if (entity == null) return NotFound(new { success = false, message = "Time slot not found." });

        var (ok, error, start, end, label) = ValidateSlot(request);
        if (!ok) return BadRequest(new { success = false, message = error });

        entity.Label = label;
        entity.StartTime = start;
        entity.EndTime = end;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Time slot updated.", data = ToSlotDto(entity) });
    }

    /// <summary>Toggle a rule's time slot active/inactive.</summary>
    [HttpPatch("rules/{ruleId:int}/slots/{id:int}/status")]
    public async Task<IActionResult> ToggleSlotActive(int ruleId, int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.PreorderSlots.FirstOrDefaultAsync(s => s.Id == id && s.PreorderRuleId == ruleId);
        if (entity == null) return NotFound(new { success = false, message = "Time slot not found." });

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = ToSlotDto(entity) });
    }

    /// <summary>Delete a rule's time slot.</summary>
    [HttpDelete("rules/{ruleId:int}/slots/{id:int}")]
    public async Task<IActionResult> DeleteSlot(int ruleId, int id)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var entity = await _db.PreorderSlots.FirstOrDefaultAsync(s => s.Id == id && s.PreorderRuleId == ruleId);
        if (entity == null) return NotFound(new { success = false, message = "Time slot not found." });

        _db.PreorderSlots.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Time slot removed." });
    }

    // ─── Preview ────────────────────────────────────────────────────────

    /// <summary>
    /// Debug/preview endpoint: resolves which preorder rule (if any) applies
    /// to a given product, optionally narrowed by pincode. Lets the admin
    /// verify targeting works as expected ahead of the consumer-side wiring.
    /// </summary>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] int productId, [FromQuery] string? pincode)
    {
        if (!await HasPermissionAsync(Resource)) return AdminUnauthorized();
        if (!await _db.Products.AnyAsync(p => p.Id == productId))
            return NotFound(new { success = false, message = "Product not found." });

        var resolved = await _preorderService.ResolveAsync(productId, pincode);
        if (resolved == null)
            return Ok(new { success = true, data = (object?)null, message = "This product is not under preorder." });

        return Ok(new
        {
            success = true,
            data = (await ToDtosAsync(new List<PreorderRule> { resolved.Rule }))[0]
        });
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private async Task<(bool ok, string? error, string scopeType)> ValidateAsync(PreorderRuleRequest request, int? excludeId)
    {
        var scopeType = (request.ScopeType ?? "").Trim().ToLowerInvariant();
        if (!ValidScopeTypes.Contains(scopeType))
            return (false, $"Scope type must be one of: {string.Join(", ", ValidScopeTypes)}.", scopeType);

        var pincode = string.IsNullOrWhiteSpace(request.Pincode) ? null : request.Pincode.Trim();

        switch (scopeType)
        {
            case "global":
                if (request.ProductId != null || request.VendorId != null || request.CategoryId != null || pincode != null)
                    return (false, "A global rule must not target a product, vendor, category, or pincode.", scopeType);
                break;
            case "product":
                if (request.ProductId == null) return (false, "Product is required for a product-scoped rule.", scopeType);
                if (!await _db.Products.AnyAsync(p => p.Id == request.ProductId))
                    return (false, "Selected product was not found.", scopeType);
                break;
            case "vendor":
                if (request.VendorId == null) return (false, "Vendor is required for a vendor-scoped rule.", scopeType);
                if (!await _db.Vendors.AnyAsync(v => v.Id == request.VendorId))
                    return (false, "Selected vendor was not found.", scopeType);
                break;
            case "category":
                if (request.CategoryId == null) return (false, "Category is required for a category-scoped rule.", scopeType);
                if (!await _db.Categories.AnyAsync(c => c.Id == request.CategoryId))
                    return (false, "Selected category was not found.", scopeType);
                break;
            case "location":
                if (pincode == null) return (false, "Pincode is required for a location-scoped rule.", scopeType);
                if (!await _db.ServiceablePincodes.AnyAsync(p => p.Pincode == pincode))
                    return (false, "Selected pincode was not found in the serviceable-pincode list.", scopeType);
                break;
        }

        if (request.WindowDays != null && request.WindowDays <= 0)
            return (false, "Window days must be greater than 0.", scopeType);

        if (request.MinLeadHours != null && request.MinLeadHours < 0)
            return (false, "Minimum lead time cannot be negative.", scopeType);

        if (request.Active)
        {
            var (ok, error) = await CheckNoDuplicateActiveAsync(scopeType, request.ProductId, request.VendorId, request.CategoryId, pincode, excludeId);
            if (!ok) return (false, error, scopeType);
        }

        return (true, null, scopeType);
    }

    private async Task<(bool ok, string? error)> CheckNoDuplicateActiveAsync(
        string scopeType, int? productId, int? vendorId, int? categoryId, string? pincode, int? excludeId)
    {
        var query = _db.PreorderRules.Where(r => r.IsActive && r.ScopeType == scopeType);
        if (excludeId != null) query = query.Where(r => r.Id != excludeId);

        var duplicateExists = scopeType switch
        {
            "global" => await query.AnyAsync(),
            "product" => await query.AnyAsync(r => r.ProductId == productId),
            "vendor" => await query.AnyAsync(r => r.VendorId == vendorId),
            "category" => await query.AnyAsync(r => r.CategoryId == categoryId),
            "location" => await query.AnyAsync(r => r.Pincode == pincode),
            _ => false
        };

        return duplicateExists
            ? (false, $"An active {scopeType} preorder rule for this target already exists.")
            : (true, null);
    }

    private static (bool ok, string? error, TimeSpan start, TimeSpan end, string label) ValidateSlot(PreorderSlotRequest request)
    {
        var label = (request.Label ?? "").Trim();
        if (label.Length == 0) return (false, "Label is required.", default, default, label);
        if (!TimeSpan.TryParse(request.StartTime, out var start))
            return (false, "Start time must be a valid time (e.g. \"09:00\").", default, default, label);
        if (!TimeSpan.TryParse(request.EndTime, out var end))
            return (false, "End time must be a valid time (e.g. \"12:00\").", default, default, label);
        if (end <= start) return (false, "End time must be after start time.", default, default, label);
        return (true, null, start, end, label);
    }

    private static object ToSlotDto(PreorderSlot s) => new
    {
        s.Id,
        s.PreorderRuleId,
        s.Label,
        StartTime = s.StartTime.ToString(@"hh\:mm"),
        EndTime = s.EndTime.ToString(@"hh\:mm"),
        s.SortOrder,
        s.IsActive,
        s.CreatedAt,
        s.UpdatedAt
    };

    /// <summary>Resolves each rule's slots plus a friendly display name for
    /// its target (batched lookups, no N+1) in one pass.</summary>
    private async Task<List<object>> ToDtosAsync(List<PreorderRule> rules)
    {
        var ruleIds = rules.Select(r => r.Id).ToList();
        var slots = await _db.PreorderSlots
            .AsNoTracking()
            .Where(s => ruleIds.Contains(s.PreorderRuleId))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .ToListAsync();
        var slotsByRule = slots.GroupBy(s => s.PreorderRuleId).ToDictionary(g => g.Key, g => g.Select(ToSlotDto).ToList());

        var productIds = rules.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var vendorIds = rules.Where(r => r.VendorId != null).Select(r => r.VendorId!.Value).Distinct().ToList();
        var categoryIds = rules.Where(r => r.CategoryId != null).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var pincodes = rules.Where(r => r.Pincode != null).Select(r => r.Pincode!).Distinct().ToList();

        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.ProductFlats.Where(f => productIds.Contains(f.ProductId))
                .GroupBy(f => f.ProductId)
                .Select(g => new { ProductId = g.Key, Name = g.Select(f => f.Name).FirstOrDefault(n => n != null) })
                .ToDictionaryAsync(x => x.ProductId, x => x.Name ?? "");

        var vendorNames = vendorIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Vendors.Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name);

        var categoryNames = categoryIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Categories.Where(c => categoryIds.Contains(c.Id))
                .Select(c => new { c.Id, Name = c.Translations.FirstOrDefault()!.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "");

        var pincodeTowns = pincodes.Count == 0
            ? new Dictionary<string, string>()
            : await _db.ServiceablePincodes.Where(p => pincodes.Contains(p.Pincode)).ToDictionaryAsync(p => p.Pincode, p => p.Town);

        return rules.Select(r => (object)new
        {
            r.Id,
            r.ScopeType,
            r.ProductId,
            ProductName = r.ProductId != null && productNames.TryGetValue(r.ProductId.Value, out var pn) ? pn : null,
            r.VendorId,
            VendorName = r.VendorId != null && vendorNames.TryGetValue(r.VendorId.Value, out var vn) ? vn : null,
            r.CategoryId,
            CategoryName = r.CategoryId != null && categoryNames.TryGetValue(r.CategoryId.Value, out var cn) ? cn : null,
            r.Pincode,
            Town = r.Pincode != null && pincodeTowns.TryGetValue(r.Pincode, out var t) ? t : null,
            r.IsActive,
            WindowDays = r.WindowDays ?? PreorderService.DefaultWindowDays,
            MinLeadHours = r.MinLeadHours ?? PreorderService.DefaultMinLeadHours,
            r.Note,
            r.CreatedAt,
            r.UpdatedAt,
            Slots = slotsByRule.TryGetValue(r.Id, out var list) ? list : new List<object>()
        }).ToList();
    }
}
