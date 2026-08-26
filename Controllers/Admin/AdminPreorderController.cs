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
        bool Active = true,
        /// <summary>Vendor scope only — optional narrowing to some categories
        /// within the vendor (subtree-inclusive, same as category scope).
        /// Empty/null means the rule covers the whole vendor.</summary>
        List<int>? CategoryIds = null,
        /// <summary>Vendor scope only — optional narrowing to some specific
        /// products within the vendor. Combined with CategoryIds via OR, if
        /// both are set.</summary>
        List<int>? ProductIds = null);

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

        if (scopeType == "vendor")
            await SyncVendorFiltersAsync(entity.Id, request.CategoryIds, request.ProductIds);

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

        // Always sync (not just when scopeType=="vendor") — if the caller
        // somehow changed a rule away from vendor scope, this defensively
        // clears any leftover filter rows rather than leaving them orphaned.
        await SyncVendorFiltersAsync(entity.Id,
            scopeType == "vendor" ? request.CategoryIds : null,
            scopeType == "vendor" ? request.ProductIds : null);

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

    // ─── Multi-select bulk create (product / category scope) ──────────────
    // Same shape as AdminDeliveryTypesController's category-prices/bulk +
    // groups/{groupId}/categories: N independent PreorderRule rows sharing
    // one GroupId, so the admin list can show/manage them as a single card
    // while each row stays fully independent (own active flag, own slots).

    public record BulkProductPreorderRuleRequest(List<int> ProductIds, int? WindowDays, int? MinLeadHours, string? Note, bool Active = true);

    /// <summary>Create the same preorder rule for several products at once.
    /// Products that already have an active product-scoped rule are skipped
    /// (not overwritten) — the response's `skipped` list names which ones and
    /// why, so the admin can edit those individually instead.</summary>
    [HttpPost("rules/products/bulk")]
    public async Task<IActionResult> CreateProductRulesBulk([FromBody] BulkProductPreorderRuleRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var productIds = (request.ProductIds ?? new List<int>()).Distinct().ToList();
        if (productIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one product." });

        var foundIds = await _db.Products.Where(p => productIds.Contains(p.Id)).Select(p => p.Id).ToListAsync();
        var missing = productIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Product id(s) not found: {string.Join(", ", missing)}." });

        if (request.WindowDays != null && request.WindowDays <= 0)
            return BadRequest(new { success = false, message = "Window days must be greater than 0." });
        if (request.MinLeadHours != null && request.MinLeadHours < 0)
            return BadRequest(new { success = false, message = "Minimum lead time cannot be negative." });

        var existingProductIds = request.Active
            ? await _db.PreorderRules
                .Where(r => r.IsActive && r.ScopeType == "product" && r.ProductId != null && productIds.Contains(r.ProductId.Value))
                .Select(r => r.ProductId!.Value).Distinct().ToListAsync()
            : new List<int>();

        var toCreate = productIds.Except(existingProductIds).ToList();
        var now = DateTime.UtcNow;
        var groupId = Guid.NewGuid().ToString();
        var entities = toCreate.Select(pid => new PreorderRule
        {
            ScopeType = "product",
            ProductId = pid,
            GroupId = groupId,
            IsActive = request.Active,
            WindowDays = request.WindowDays,
            MinLeadHours = request.MinLeadHours,
            Note = request.Note,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.PreorderRules.AddRange(entities);
        await _db.SaveChangesAsync();

        var skipped = existingProductIds.Count == 0
            ? new List<object>()
            : (await _db.ProductFlats.Where(f => existingProductIds.Contains(f.ProductId))
                .GroupBy(f => f.ProductId)
                .Select(g => new { productId = g.Key, productName = g.Select(f => f.Name).FirstOrDefault(n => n != null) ?? "" })
                .ToListAsync()).Select(x => (object)x).ToList();

        var message = existingProductIds.Count == 0
            ? $"Added to {entities.Count} product{(entities.Count == 1 ? "" : "s")}."
            : $"Added to {entities.Count} product{(entities.Count == 1 ? "" : "s")}; skipped {existingProductIds.Count} that already had an active rule.";

        return Ok(new { success = true, message, data = await ToDtosAsync(entities), skipped });
    }

    public record AddProductsToPreorderGroupRequest(List<int> ProductIds);

    /// <summary>Add more products to an existing bulk-created preorder-rule
    /// group. New rows copy the group's window/lead-time/note/active state.
    /// Products that already have an active product-scoped rule are skipped.</summary>
    [HttpPost("rules/products/groups/{groupId}/products")]
    public async Task<IActionResult> AddProductsToPreorderGroup(string groupId, [FromBody] AddProductsToPreorderGroupRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var template = await _db.PreorderRules
            .Where(r => r.ScopeType == "product" && r.GroupId == groupId)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync();
        if (template == null) return NotFound(new { success = false, message = "Preorder rule group not found." });

        var productIds = (request.ProductIds ?? new List<int>()).Distinct().ToList();
        if (productIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one product." });

        var foundIds = await _db.Products.Where(p => productIds.Contains(p.Id)).Select(p => p.Id).ToListAsync();
        var missing = productIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Product id(s) not found: {string.Join(", ", missing)}." });

        var existingProductIds = await _db.PreorderRules
            .Where(r => r.IsActive && r.ScopeType == "product" && r.ProductId != null && productIds.Contains(r.ProductId.Value))
            .Select(r => r.ProductId!.Value).Distinct().ToListAsync();
        var toCreate = productIds.Except(existingProductIds).ToList();

        var now = DateTime.UtcNow;
        var entities = toCreate.Select(pid => new PreorderRule
        {
            ScopeType = "product",
            ProductId = pid,
            GroupId = groupId,
            IsActive = template.IsActive,
            WindowDays = template.WindowDays,
            MinLeadHours = template.MinLeadHours,
            Note = template.Note,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.PreorderRules.AddRange(entities);
        await _db.SaveChangesAsync();
        await CopyGroupSlotsToNewRulesAsync(template.Id, entities);

        var skipped = existingProductIds.Count == 0
            ? new List<object>()
            : (await _db.ProductFlats.Where(f => existingProductIds.Contains(f.ProductId))
                .GroupBy(f => f.ProductId)
                .Select(g => new { productId = g.Key, productName = g.Select(f => f.Name).FirstOrDefault(n => n != null) ?? "" })
                .ToListAsync()).Select(x => (object)x).ToList();

        var message = existingProductIds.Count == 0
            ? $"Added {entities.Count} more product{(entities.Count == 1 ? "" : "s")}."
            : $"Added {entities.Count} more product{(entities.Count == 1 ? "" : "s")}; skipped {existingProductIds.Count} that already had an active rule.";

        return Ok(new { success = true, message, data = await ToDtosAsync(entities), skipped });
    }

    public record BulkCategoryPreorderRuleRequest(List<int> CategoryIds, int? WindowDays, int? MinLeadHours, string? Note, bool Active = true);

    /// <summary>Create the same preorder rule for several categories at once.
    /// Categories that already have an active category-scoped rule are
    /// skipped (not overwritten).</summary>
    [HttpPost("rules/categories/bulk")]
    public async Task<IActionResult> CreateCategoryRulesBulk([FromBody] BulkCategoryPreorderRuleRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var categoryIds = (request.CategoryIds ?? new List<int>()).Distinct().ToList();
        if (categoryIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one category." });

        var foundIds = await _db.Categories.Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missing = categoryIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Category id(s) not found: {string.Join(", ", missing)}." });

        if (request.WindowDays != null && request.WindowDays <= 0)
            return BadRequest(new { success = false, message = "Window days must be greater than 0." });
        if (request.MinLeadHours != null && request.MinLeadHours < 0)
            return BadRequest(new { success = false, message = "Minimum lead time cannot be negative." });

        var existingCategoryIds = request.Active
            ? await _db.PreorderRules
                .Where(r => r.IsActive && r.ScopeType == "category" && r.CategoryId != null && categoryIds.Contains(r.CategoryId.Value))
                .Select(r => r.CategoryId!.Value).Distinct().ToListAsync()
            : new List<int>();

        var toCreate = categoryIds.Except(existingCategoryIds).ToList();
        var now = DateTime.UtcNow;
        var groupId = Guid.NewGuid().ToString();
        var entities = toCreate.Select(cid => new PreorderRule
        {
            ScopeType = "category",
            CategoryId = cid,
            GroupId = groupId,
            IsActive = request.Active,
            WindowDays = request.WindowDays,
            MinLeadHours = request.MinLeadHours,
            Note = request.Note,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.PreorderRules.AddRange(entities);
        await _db.SaveChangesAsync();

        var skipped = existingCategoryIds.Count == 0
            ? new List<object>()
            : (await _db.Categories.Where(c => existingCategoryIds.Contains(c.Id))
                .Select(c => new { categoryId = c.Id, categoryName = c.Translations.FirstOrDefault()!.Name })
                .ToListAsync()).Select(x => (object)x).ToList();

        var message = existingCategoryIds.Count == 0
            ? $"Added to {entities.Count} categor{(entities.Count == 1 ? "y" : "ies")}."
            : $"Added to {entities.Count} categor{(entities.Count == 1 ? "y" : "ies")}; skipped {existingCategoryIds.Count} that already had an active rule.";

        return Ok(new { success = true, message, data = await ToDtosAsync(entities), skipped });
    }

    public record AddCategoriesToPreorderGroupRequest(List<int> CategoryIds);

    /// <summary>Add more categories to an existing bulk-created preorder-rule
    /// group. New rows copy the group's window/lead-time/note/active state.</summary>
    [HttpPost("rules/categories/groups/{groupId}/categories")]
    public async Task<IActionResult> AddCategoriesToPreorderGroup(string groupId, [FromBody] AddCategoriesToPreorderGroupRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var template = await _db.PreorderRules
            .Where(r => r.ScopeType == "category" && r.GroupId == groupId)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync();
        if (template == null) return NotFound(new { success = false, message = "Preorder rule group not found." });

        var categoryIds = (request.CategoryIds ?? new List<int>()).Distinct().ToList();
        if (categoryIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one category." });

        var foundIds = await _db.Categories.Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missing = categoryIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Category id(s) not found: {string.Join(", ", missing)}." });

        var existingCategoryIds = await _db.PreorderRules
            .Where(r => r.IsActive && r.ScopeType == "category" && r.CategoryId != null && categoryIds.Contains(r.CategoryId.Value))
            .Select(r => r.CategoryId!.Value).Distinct().ToListAsync();
        var toCreate = categoryIds.Except(existingCategoryIds).ToList();

        var now = DateTime.UtcNow;
        var entities = toCreate.Select(cid => new PreorderRule
        {
            ScopeType = "category",
            CategoryId = cid,
            GroupId = groupId,
            IsActive = template.IsActive,
            WindowDays = template.WindowDays,
            MinLeadHours = template.MinLeadHours,
            Note = template.Note,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.PreorderRules.AddRange(entities);
        await _db.SaveChangesAsync();
        await CopyGroupSlotsToNewRulesAsync(template.Id, entities);

        var skipped = existingCategoryIds.Count == 0
            ? new List<object>()
            : (await _db.Categories.Where(c => existingCategoryIds.Contains(c.Id))
                .Select(c => new { categoryId = c.Id, categoryName = c.Translations.FirstOrDefault()!.Name })
                .ToListAsync()).Select(x => (object)x).ToList();

        var message = existingCategoryIds.Count == 0
            ? $"Added {entities.Count} more categor{(entities.Count == 1 ? "y" : "ies")}."
            : $"Added {entities.Count} more categor{(entities.Count == 1 ? "y" : "ies")}; skipped {existingCategoryIds.Count} that already had an active rule.";

        return Ok(new { success = true, message, data = await ToDtosAsync(entities), skipped });
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

    // ─── Slots for a whole group (product/category multi-select) ─────────
    // A bulk-created group is N independent PreorderRule rows, each with its
    // own slots table row — see the schema note above CreateProductRulesBulk.
    // Without this, an admin managing a 5-product group would have to open
    // "Time Slots" 5 times and re-enter the same windows by hand. These
    // endpoints fan one slot operation out across every row that shares
    // groupId, so the admin manages the group's slots exactly once. Rows are
    // matched to "the same logical slot" by (StartTime, EndTime) — the pair
    // stays in sync across the group as long as slots are only ever added/
    // edited/removed through these group endpoints (or a fresh member added
    // via AddProductsToPreorderGroup/AddCategoriesToPreorderGroup, which now
    // copies the template rule's existing slots so it starts in sync too).

    public record UpdateGroupSlotRequest(string MatchStartTime, string MatchEndTime, string Label, string StartTime, string EndTime, int SortOrder = 0, bool Active = true);

    private async Task<List<PreorderRule>> GroupRulesAsync(string groupId) =>
        await _db.PreorderRules.Where(r => r.GroupId == groupId).ToListAsync();

    /// <summary>Add the same time slot to every rule in a bulk-created group.</summary>
    [HttpPost("rules/groups/{groupId}/slots")]
    public async Task<IActionResult> CreateGroupSlot(string groupId, [FromBody] PreorderSlotRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var rules = await GroupRulesAsync(groupId);
        if (rules.Count == 0) return NotFound(new { success = false, message = "Preorder rule group not found." });

        var (ok, error, start, end, label) = ValidateSlot(request);
        if (!ok) return BadRequest(new { success = false, message = error });

        var now = DateTime.UtcNow;
        var entities = rules.Select(r => new PreorderSlot
        {
            PreorderRuleId = r.Id,
            Label = label,
            StartTime = start,
            EndTime = end,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.PreorderSlots.AddRange(entities);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = $"Time slot added to {entities.Count} rule{(entities.Count == 1 ? "" : "s")}.", data = entities.Select(ToSlotDto) });
    }

    /// <summary>Update the matching time slot (by its current start/end) across
    /// every rule in a bulk-created group. Rules with no matching slot are
    /// left untouched rather than failing the whole request.</summary>
    [HttpPut("rules/groups/{groupId}/slots")]
    public async Task<IActionResult> UpdateGroupSlot(string groupId, [FromBody] UpdateGroupSlotRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var rules = await GroupRulesAsync(groupId);
        if (rules.Count == 0) return NotFound(new { success = false, message = "Preorder rule group not found." });

        if (!TimeSpan.TryParse(request.MatchStartTime, out var matchStart) || !TimeSpan.TryParse(request.MatchEndTime, out var matchEnd))
            return BadRequest(new { success = false, message = "Invalid slot to match." });

        var (ok, error, start, end, label) = ValidateSlot(new PreorderSlotRequest(request.Label, request.StartTime, request.EndTime, request.SortOrder, request.Active));
        if (!ok) return BadRequest(new { success = false, message = error });

        var ruleIds = rules.Select(r => r.Id).ToList();
        var slots = await _db.PreorderSlots
            .Where(s => ruleIds.Contains(s.PreorderRuleId) && s.StartTime == matchStart && s.EndTime == matchEnd)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var s in slots)
        {
            s.Label = label;
            s.StartTime = start;
            s.EndTime = end;
            s.SortOrder = request.SortOrder;
            s.IsActive = request.Active;
            s.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = $"Time slot updated on {slots.Count} rule{(slots.Count == 1 ? "" : "s")}.", data = slots.Select(ToSlotDto) });
    }

    /// <summary>Toggle the matching time slot (by start/end) active/inactive
    /// across every rule in a bulk-created group.</summary>
    [HttpPatch("rules/groups/{groupId}/slots/status")]
    public async Task<IActionResult> ToggleGroupSlotActive(string groupId, [FromQuery] string startTime, [FromQuery] string endTime, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var rules = await GroupRulesAsync(groupId);
        if (rules.Count == 0) return NotFound(new { success = false, message = "Preorder rule group not found." });

        if (!TimeSpan.TryParse(startTime, out var start) || !TimeSpan.TryParse(endTime, out var end))
            return BadRequest(new { success = false, message = "Invalid slot to match." });

        var ruleIds = rules.Select(r => r.Id).ToList();
        var slots = await _db.PreorderSlots
            .Where(s => ruleIds.Contains(s.PreorderRuleId) && s.StartTime == start && s.EndTime == end)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var s in slots)
        {
            s.IsActive = request.Active;
            s.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = $"Status updated on {slots.Count} rule{(slots.Count == 1 ? "" : "s")}.", data = slots.Select(ToSlotDto) });
    }

    /// <summary>Delete the matching time slot (by start/end) from every rule
    /// in a bulk-created group.</summary>
    [HttpDelete("rules/groups/{groupId}/slots")]
    public async Task<IActionResult> DeleteGroupSlot(string groupId, [FromQuery] string startTime, [FromQuery] string endTime)
    {
        if (!await HasPermissionAsync(Resource, requireWrite: true)) return AdminForbidden(Resource);

        var rules = await GroupRulesAsync(groupId);
        if (rules.Count == 0) return NotFound(new { success = false, message = "Preorder rule group not found." });

        if (!TimeSpan.TryParse(startTime, out var start) || !TimeSpan.TryParse(endTime, out var end))
            return BadRequest(new { success = false, message = "Invalid slot to match." });

        var ruleIds = rules.Select(r => r.Id).ToList();
        var slots = await _db.PreorderSlots
            .Where(s => ruleIds.Contains(s.PreorderRuleId) && s.StartTime == start && s.EndTime == end)
            .ToListAsync();

        _db.PreorderSlots.RemoveRange(slots);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = $"Time slot removed from {slots.Count} rule{(slots.Count == 1 ? "" : "s")}." });
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

        if (scopeType != "vendor" && (request.CategoryIds is { Count: > 0 } || request.ProductIds is { Count: > 0 }))
            return (false, "categoryIds/productIds are only allowed for vendor-scoped rules.", scopeType);

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
                if (request.CategoryIds is { Count: > 0 })
                {
                    var foundCatIds = await _db.Categories.Where(c => request.CategoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
                    var missingCat = request.CategoryIds.Except(foundCatIds).ToList();
                    if (missingCat.Count > 0)
                        return (false, $"Category id(s) not found: {string.Join(", ", missingCat)}.", scopeType);
                }
                if (request.ProductIds is { Count: > 0 })
                {
                    var foundProdIds = await _db.Products.Where(p => request.ProductIds.Contains(p.Id)).Select(p => p.Id).ToListAsync();
                    var missingProd = request.ProductIds.Except(foundProdIds).ToList();
                    if (missingProd.Count > 0)
                        return (false, $"Product id(s) not found: {string.Join(", ", missingProd)}.", scopeType);
                }
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

    /// <summary>Replaces a vendor-scoped rule's optional category/product
    /// narrowing filters wholesale — simplest correct approach for an edit,
    /// avoids diffing. Passing null/empty for both clears the rule back to
    /// matching its whole vendor.</summary>
    private async Task SyncVendorFiltersAsync(int ruleId, List<int>? categoryIds, List<int>? productIds)
    {
        var now = DateTime.UtcNow;
        _db.PreorderRuleCategories.RemoveRange(_db.PreorderRuleCategories.Where(x => x.PreorderRuleId == ruleId));
        _db.PreorderRuleProducts.RemoveRange(_db.PreorderRuleProducts.Where(x => x.PreorderRuleId == ruleId));
        if (categoryIds is { Count: > 0 })
            _db.PreorderRuleCategories.AddRange(categoryIds.Distinct().Select(cid =>
                new PreorderRuleCategory { PreorderRuleId = ruleId, CategoryId = cid, CreatedAt = now }));
        if (productIds is { Count: > 0 })
            _db.PreorderRuleProducts.AddRange(productIds.Distinct().Select(pid =>
                new PreorderRuleProduct { PreorderRuleId = ruleId, ProductId = pid, CreatedAt = now }));
        await _db.SaveChangesAsync();
    }

    /// <summary>Copies the template rule's existing time slots onto every
    /// newly-created row when a group grows — otherwise a product/category
    /// added to an already-configured group would silently start with zero
    /// slots while its siblings already have some.</summary>
    private async Task CopyGroupSlotsToNewRulesAsync(int templateRuleId, List<PreorderRule> newRules)
    {
        if (newRules.Count == 0) return;

        var templateSlots = await _db.PreorderSlots.AsNoTracking()
            .Where(s => s.PreorderRuleId == templateRuleId)
            .ToListAsync();
        if (templateSlots.Count == 0) return;

        var now = DateTime.UtcNow;
        var copies = newRules.SelectMany(r => templateSlots.Select(s => new PreorderSlot
        {
            PreorderRuleId = r.Id,
            Label = s.Label,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            SortOrder = s.SortOrder,
            IsActive = s.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        })).ToList();
        _db.PreorderSlots.AddRange(copies);
        await _db.SaveChangesAsync();
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

        // Vendor rules' optional narrowing filters — batched, keyed by rule
        // id, and folded into the productIds/categoryIds lookups below so
        // filter names resolve in the same round trip as everything else.
        var vendorRuleIds = rules.Where(r => r.ScopeType == "vendor").Select(r => r.Id).ToList();
        var vendorCatFilterRows = vendorRuleIds.Count == 0
            ? new List<PreorderRuleCategory>()
            : await _db.PreorderRuleCategories.AsNoTracking().Where(x => vendorRuleIds.Contains(x.PreorderRuleId)).ToListAsync();
        var vendorProdFilterRows = vendorRuleIds.Count == 0
            ? new List<PreorderRuleProduct>()
            : await _db.PreorderRuleProducts.AsNoTracking().Where(x => vendorRuleIds.Contains(x.PreorderRuleId)).ToListAsync();
        var vendorCatFiltersByRule = vendorCatFilterRows.GroupBy(x => x.PreorderRuleId).ToDictionary(g => g.Key, g => g.Select(x => x.CategoryId).ToList());
        var vendorProdFiltersByRule = vendorProdFilterRows.GroupBy(x => x.PreorderRuleId).ToDictionary(g => g.Key, g => g.Select(x => x.ProductId).ToList());

        var productIds = rules.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value)
            .Concat(vendorProdFilterRows.Select(x => x.ProductId)).Distinct().ToList();
        var vendorIds = rules.Where(r => r.VendorId != null).Select(r => r.VendorId!.Value).Distinct().ToList();
        var categoryIds = rules.Where(r => r.CategoryId != null).Select(r => r.CategoryId!.Value)
            .Concat(vendorCatFilterRows.Select(x => x.CategoryId)).Distinct().ToList();
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
            r.GroupId,
            WindowDays = r.WindowDays ?? PreorderService.DefaultWindowDays,
            MinLeadHours = r.MinLeadHours ?? PreorderService.DefaultMinLeadHours,
            r.Note,
            r.CreatedAt,
            r.UpdatedAt,
            Slots = slotsByRule.TryGetValue(r.Id, out var list) ? list : new List<object>(),
            VendorCategoryFilters = (vendorCatFiltersByRule.TryGetValue(r.Id, out var vcf) ? vcf : new List<int>())
                .Select(cid => new { categoryId = cid, categoryName = categoryNames.GetValueOrDefault(cid, "") }).ToList(),
            VendorProductFilters = (vendorProdFiltersByRule.TryGetValue(r.Id, out var vpf) ? vpf : new List<int>())
                .Select(pid => new { productId = pid, productName = productNames.GetValueOrDefault(pid, "") }).ToList()
        }).ToList();
    }
}
