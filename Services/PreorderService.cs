using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;

namespace DOSApi.Services;

/// <summary>
/// Resolves whether a product is currently under preorder and, if so, which
/// PreorderRule governs it. Backs AdminPreorderController's "resolve"
/// preview endpoint AND the consumer-facing cart/checkout preorder gate
/// (see ResolveForCartAsync, CheckoutService.SavePreorderSelectionAsync,
/// CheckoutService.CreateOrderFromCartAsync).
/// </summary>
public class PreorderService
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    /// <summary>Used when a matching rule's WindowDays is null.</summary>
    public const int DefaultWindowDays = 6;

    /// <summary>Used when a matching rule's MinLeadHours is null.</summary>
    public const int DefaultMinLeadHours = 12;

    private const double IstOffsetHours = 5.5;

    /// <summary>India Standard Time is a fixed +5:30 offset (no DST), so a
    /// constant is safe here. Every other timestamp in this codebase is
    /// plain UTC (audit fields like CreatedAt), which is correct for them —
    /// but a preorder delivery *date* is inherently a calendar-day concept
    /// picked by the customer on their own (IST) device. Comparing it
    /// against `DateTime.UtcNow.Date` instead of this would silently reject
    /// the last day of an otherwise-valid window during the ~5.5 hours each
    /// night (00:00-05:30 IST) where the UTC calendar day is still
    /// "yesterday" relative to the customer's.</summary>
    public static DateTime TodayIst() => DateTime.UtcNow.AddHours(IstOffsetHours).Date;

    /// <summary>
    /// Whether a slot on the given IST calendar date still clears the
    /// rule's minimum-notice cutoff — this is what makes same-day delivery
    /// reachable (an evening slot booked in the morning) while still
    /// respecting prep time. `dateIst` + `slot.StartTime` is a wall-clock
    /// IST instant; convert to UTC (subtract the fixed offset) before
    /// comparing against `DateTime.UtcNow`.
    /// </summary>
    public static bool IsSlotSelectable(DateTime dateIst, PreorderSlot slot, int minLeadHours)
    {
        var slotStartUtc = dateIst.Date.Add(slot.StartTime).AddHours(-IstOffsetHours);
        return slotStartUtc >= DateTime.UtcNow.AddHours(minLeadHours);
    }

    /// <summary>Lower = more specific. Mirrors the precedence walk in
    /// ResolveAsync: Product > Vendor > Category > Location > Global.</summary>
    private static readonly Dictionary<string, int> ScopeSpecificity = new()
    {
        ["product"] = 0,
        ["vendor"] = 1,
        ["category"] = 2,
        ["location"] = 3,
        ["global"] = 4,
    };

    public PreorderService(DOSDbContext db, ProductService productService)
    {
        _db = db;
        _productService = productService;
    }

    public record ResolvedPreorder(PreorderRule Rule, List<PreorderSlot> Slots);

    /// <summary>
    /// Precedence: Product > Vendor > Category (incl. subcategories) >
    /// Location (pincode) > Global. Returns the first active rule that
    /// matches, or null if the product isn't under preorder at all.
    /// </summary>
    public async Task<ResolvedPreorder?> ResolveAsync(int productId, string? pincode)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Include(p => p.Categories)
            .Include(p => p.Inventories)
            .FirstOrDefaultAsync(p => p.Id == productId);
        if (product == null) return null;

        var rules = await _db.PreorderRules
            .AsNoTracking()
            .Where(r => r.IsActive)
            .ToListAsync();
        if (rules.Count == 0) return null;

        var productRule = rules.FirstOrDefault(r => r.ScopeType == "product" && r.ProductId == productId);
        if (productRule != null) return await BuildResultAsync(productRule);

        var vendorIds = product.Inventories.Select(i => i.VendorId).Distinct().ToList();
        var vendorRule = rules.FirstOrDefault(r => r.ScopeType == "vendor" && r.VendorId != null && vendorIds.Contains(r.VendorId.Value));
        if (vendorRule != null) return await BuildResultAsync(vendorRule);

        var productCategoryIds = product.Categories.Select(c => c.Id).ToHashSet();
        foreach (var rule in rules.Where(r => r.ScopeType == "category" && r.CategoryId != null))
        {
            var subtreeIds = await _productService.GetSubtreeCategoryIdsAsync(rule.CategoryId!.Value);
            if (subtreeIds.Any(id => productCategoryIds.Contains(id)))
                return await BuildResultAsync(rule);
        }

        if (!string.IsNullOrWhiteSpace(pincode))
        {
            var locationRule = rules.FirstOrDefault(r => r.ScopeType == "location" && r.Pincode == pincode);
            if (locationRule != null) return await BuildResultAsync(locationRule);
        }

        var globalRule = rules.FirstOrDefault(r => r.ScopeType == "global");
        if (globalRule != null) return await BuildResultAsync(globalRule);

        return null;
    }

    /// <summary>One distinct preorder rule's footprint within a cart — its
    /// own slots and the specific products in the cart it governs. A cart
    /// can contain several of these at once (e.g. two preorder items under
    /// two different admin rules with different delivery windows) — each
    /// becomes its own delivery-slot selection and, at placement time, its
    /// own Order.</summary>
    public record CartPreorderGroup(PreorderRule Rule, List<PreorderSlot> Slots, List<int> ProductIds);

    /// <summary>Cart-wide resolution. Groups is one entry per distinct rule
    /// that any cart item resolves to — NOT collapsed to a single "most
    /// specific wins" rule, because two preorder items can legitimately
    /// belong to two different rules with different slot sets, and forcing
    /// them under one rule's slots would silently misrepresent the other
    /// item's real delivery window. PreorderProductIds is the flat union of
    /// every product in any group — used to flag which cart items show a
    /// "preorder" badge regardless of which group they're in.</summary>
    public record CartPreorderResolution(List<CartPreorderGroup> Groups, HashSet<int> PreorderProductIds)
    {
        public bool RequiresPreorder => Groups.Count > 0;
    }

    /// <summary>
    /// Resolves preorder status across a whole cart by bucketing each
    /// product under the rule it *individually* resolves to (via
    /// ResolveAsync's per-product precedence walk) — one bucket per distinct
    /// rule id, not one winner for the whole cart.
    /// </summary>
    public async Task<CartPreorderResolution> ResolveForCartAsync(Models.Cart.Cart cart, string? pincode)
    {
        var ruleById = new Dictionary<int, PreorderRule>();
        var slotsById = new Dictionary<int, List<PreorderSlot>>();
        var productIdsById = new Dictionary<int, List<int>>();
        var preorderProductIds = new HashSet<int>();

        foreach (var productId in cart.Items.Select(i => i.ProductId).Distinct())
        {
            var resolved = await ResolveAsync(productId, pincode);
            if (resolved == null) continue;
            preorderProductIds.Add(productId);

            if (!productIdsById.TryGetValue(resolved.Rule.Id, out var productIds))
            {
                productIds = new List<int>();
                productIdsById[resolved.Rule.Id] = productIds;
                ruleById[resolved.Rule.Id] = resolved.Rule;
                slotsById[resolved.Rule.Id] = resolved.Slots;
            }
            productIds.Add(productId);
        }

        var groups = productIdsById.Keys
            .OrderBy(ruleId => Specificity(ruleById[ruleId].ScopeType))
            .ThenBy(ruleId => ruleId)
            .Select(ruleId => new CartPreorderGroup(ruleById[ruleId], slotsById[ruleId], productIdsById[ruleId]))
            .ToList();

        return new CartPreorderResolution(groups, preorderProductIds);
    }

    private static int Specificity(string scopeType) =>
        ScopeSpecificity.TryGetValue(scopeType, out var v) ? v : int.MaxValue;

    private async Task<ResolvedPreorder> BuildResultAsync(PreorderRule rule)
    {
        var slots = await _db.PreorderSlots
            .AsNoTracking()
            .Where(s => s.PreorderRuleId == rule.Id && s.IsActive)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync();
        return new ResolvedPreorder(rule, slots);
    }
}
