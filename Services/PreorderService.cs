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
    /// <param name="preloadedRules">Pass the active-rules list when the
    /// caller is resolving several products in a row (see
    /// ResolveForCartAsync) — this table is small but the remote DB's
    /// per-round-trip latency is not, so refetching it once per distinct
    /// cart product instead of once per call meaningfully cuts total round
    /// trips. Null (the default) fetches it fresh, for single-product
    /// callers like the admin "resolve" preview endpoint.</param>
    public async Task<ResolvedPreorder?> ResolveAsync(int productId, string? pincode, List<PreorderRule>? preloadedRules = null)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Include(p => p.Categories)
            .FirstOrDefaultAsync(p => p.Id == productId);
        if (product == null) return null;

        var rules = preloadedRules ?? await _db.PreorderRules
            .AsNoTracking()
            .Where(r => r.IsActive)
            .ToListAsync();
        if (rules.Count == 0) return null;

        // A product-scoped rule set on a CONFIGURABLE parent (e.g. "Mutton")
        // must also cover its variant children (e.g. "Mutton 1 kg") — those
        // are what customers actually add to cart (CartItem.ProductId is the
        // variant's own id, never the parent's), and the admin product
        // picker doesn't stop someone from picking the parent listing. An
        // exact-id-only match here silently drops preorder status for every
        // variant of a parent-scoped rule.
        var productRule = rules.FirstOrDefault(r => r.ScopeType == "product" &&
            (r.ProductId == productId || (product.ParentId != null && r.ProductId == product.ParentId)));
        if (productRule != null) return await BuildResultAsync(productRule);

        var productCategoryIds = product.Categories.Select(c => c.Id).ToHashSet();

        // Vendor.cs's own comment says it plainly: "there is no FK from
        // products to this table; matching happens by name." Vendor.Name is
        // backfilled from the vendor_en/vendor_te name embedded in a
        // product's own `additional` JSON — see ProductService.ExtractVendorName,
        // the exact same lookup VendorAggregationService/AdminVendorsController
        // use everywhere else a product needs to be tied to a vendor.
        // Product.Inventories.VendorId is an unrelated concept (stock
        // allocation, not "who sells this") and was never a valid way to
        // find a product's vendor — a plain whole-vendor rule silently
        // matched nothing before this fix. Only top-level products carry
        // this field; a variant inherits its parent's, same as the
        // product-scope match above.
        var vendorName = ProductService.ExtractVendorName(product.Additional, "en");
        if (vendorName == null && product.ParentId != null)
        {
            var parentAdditional = await _db.Products.AsNoTracking()
                .Where(p => p.Id == product.ParentId)
                .Select(p => p.Additional)
                .FirstOrDefaultAsync();
            vendorName = ProductService.ExtractVendorName(parentAdditional, "en");
        }

        // A vendor-scoped rule matches its whole vendor by default (unchanged
        // from before this feature existed). It can optionally be narrowed
        // to just some categories and/or some products within that vendor —
        // see preorder_rule_categories/preorder_rule_products. Either filter
        // present makes the rule match on category-subtree OR explicit
        // product id (union), not both required.
        var candidateVendorRules = new List<PreorderRule>();
        if (vendorName != null)
        {
            var vendorScopedRules = rules.Where(r => r.ScopeType == "vendor" && r.VendorId != null).ToList();
            if (vendorScopedRules.Count > 0)
            {
                var vendorIdsInRules = vendorScopedRules.Select(r => r.VendorId!.Value).Distinct().ToList();
                var vendorNamesById = await _db.Vendors.AsNoTracking()
                    .Where(v => vendorIdsInRules.Contains(v.Id))
                    .ToDictionaryAsync(v => v.Id, v => v.Name);
                candidateVendorRules = vendorScopedRules
                    .Where(r => vendorNamesById.TryGetValue(r.VendorId!.Value, out var vn) &&
                        string.Equals(vn, vendorName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }
        if (candidateVendorRules.Count > 0)
        {
            var candidateRuleIds = candidateVendorRules.Select(r => r.Id).ToList();
            var catFiltersByRule = (await _db.PreorderRuleCategories.AsNoTracking()
                    .Where(x => candidateRuleIds.Contains(x.PreorderRuleId)).ToListAsync())
                .GroupBy(x => x.PreorderRuleId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CategoryId).ToList());
            var prodFiltersByRule = (await _db.PreorderRuleProducts.AsNoTracking()
                    .Where(x => candidateRuleIds.Contains(x.PreorderRuleId)).ToListAsync())
                .GroupBy(x => x.PreorderRuleId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ProductId).ToList());

            foreach (var rule in candidateVendorRules)
            {
                var catIds = catFiltersByRule.GetValueOrDefault(rule.Id, new List<int>());
                var prodIds = prodFiltersByRule.GetValueOrDefault(rule.Id, new List<int>());

                if (catIds.Count == 0 && prodIds.Count == 0)
                    return await BuildResultAsync(rule);

                var matchesProduct = prodIds.Contains(productId) ||
                    (product.ParentId != null && prodIds.Contains(product.ParentId.Value));

                var matchesCategory = false;
                foreach (var catId in catIds)
                {
                    var vendorFilterSubtreeIds = await _productService.GetSubtreeCategoryIdsAsync(catId);
                    if (vendorFilterSubtreeIds.Any(id => productCategoryIds.Contains(id)))
                    {
                        matchesCategory = true;
                        break;
                    }
                }

                if (matchesProduct || matchesCategory) return await BuildResultAsync(rule);
            }
        }

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

        // Fetched once here rather than once per distinct product inside
        // ResolveAsync — same data every time within one cart resolution,
        // and each round trip to the remote DB is expensive enough that this
        // is a real saving on carts with more than one preorder-eligible item.
        var rules = await _db.PreorderRules.AsNoTracking().Where(r => r.IsActive).ToListAsync();
        if (rules.Count == 0) return new CartPreorderResolution(new List<CartPreorderGroup>(), preorderProductIds);

        foreach (var productId in cart.Items.Select(i => i.ProductId).Distinct())
        {
            var resolved = await ResolveAsync(productId, pincode, rules);
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

    /// <summary>
    /// Batched "is this product under preorder?" check for a whole LISTING
    /// page (20-44 products), not a cart's typically-few distinct products.
    /// Calling ResolveAsync in a loop here would be a real N+1 problem — it
    /// does 2+ round trips per product on its own, on top of whatever a
    /// vendor/category-scope match adds. This walks every active rule ONCE
    /// and matches it against the whole product batch in memory instead,
    /// same batching shape AdminPreorderController.ToDtosAsync already uses
    /// for its own per-rule name lookups. Callers just need is-it-preorder
    /// for a listing card's button label — which specific rule/slots apply
    /// is resolved later, at add-to-cart time, via the existing
    /// ResolveAsync/ResolveForCartAsync (the two must agree; see the
    /// precedence walk mirrored below).
    /// </summary>
    public async Task<HashSet<int>> ResolveListPreorderStatusAsync(List<int> productIds, string? pincode)
    {
        var result = new HashSet<int>();
        var distinctIds = productIds.Distinct().ToList();
        if (distinctIds.Count == 0) return result;

        var rules = await _db.PreorderRules.AsNoTracking().Where(r => r.IsActive).ToListAsync();
        if (rules.Count == 0) return result;

        // Global scope matches every product unconditionally (see
        // ResolveAsync) — no need to load anything else.
        if (rules.Any(r => r.ScopeType == "global"))
        {
            foreach (var id in distinctIds) result.Add(id);
            return result;
        }

        var products = await _db.Products.AsNoTracking()
            .Where(p => distinctIds.Contains(p.Id))
            .Include(p => p.Categories)
            .ToListAsync();
        if (products.Count == 0) return result;

        var parentIds = products.Where(p => p.ParentId != null).Select(p => p.ParentId!.Value).Distinct().ToList();
        var parentAdditionalById = parentIds.Count == 0
            ? new Dictionary<int, string?>()
            : await _db.Products.AsNoTracking().Where(p => parentIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Additional);

        var productScopedIds = rules.Where(r => r.ScopeType == "product" && r.ProductId != null)
            .Select(r => r.ProductId!.Value).ToHashSet();

        var vendorScopedRules = rules.Where(r => r.ScopeType == "vendor" && r.VendorId != null).ToList();
        var vendorNamesById = new Dictionary<int, string>();
        var vendorCatFiltersByRule = new Dictionary<int, List<int>>();
        var vendorProdFiltersByRule = new Dictionary<int, List<int>>();
        if (vendorScopedRules.Count > 0)
        {
            var vendorIds = vendorScopedRules.Select(r => r.VendorId!.Value).Distinct().ToList();
            vendorNamesById = await _db.Vendors.AsNoTracking()
                .Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name);
            var vendorRuleIds = vendorScopedRules.Select(r => r.Id).ToList();
            vendorCatFiltersByRule = (await _db.PreorderRuleCategories.AsNoTracking()
                    .Where(x => vendorRuleIds.Contains(x.PreorderRuleId)).ToListAsync())
                .GroupBy(x => x.PreorderRuleId).ToDictionary(g => g.Key, g => g.Select(x => x.CategoryId).ToList());
            vendorProdFiltersByRule = (await _db.PreorderRuleProducts.AsNoTracking()
                    .Where(x => vendorRuleIds.Contains(x.PreorderRuleId)).ToListAsync())
                .GroupBy(x => x.PreorderRuleId).ToDictionary(g => g.Key, g => g.Select(x => x.ProductId).ToList());
        }

        var categoryScopedRules = rules.Where(r => r.ScopeType == "category" && r.CategoryId != null).ToList();

        // Category subtree expansion is the same call regardless of whether
        // it comes from a plain category-scope rule or a vendor-scope
        // rule's category filter — cached so the same category id is never
        // expanded twice within one listing resolution.
        var subtreeCache = new Dictionary<int, HashSet<int>>();
        async Task<HashSet<int>> SubtreeOf(int categoryId)
        {
            if (!subtreeCache.TryGetValue(categoryId, out var ids))
            {
                ids = (await _productService.GetSubtreeCategoryIdsAsync(categoryId)).ToHashSet();
                subtreeCache[categoryId] = ids;
            }
            return ids;
        }

        var locationRule = !string.IsNullOrWhiteSpace(pincode)
            ? rules.FirstOrDefault(r => r.ScopeType == "location" && r.Pincode == pincode)
            : null;

        foreach (var product in products)
        {
            // Product scope (own id or parent id — variants inherit a
            // parent-scoped rule, same as ResolveAsync).
            if (productScopedIds.Contains(product.Id) ||
                (product.ParentId != null && productScopedIds.Contains(product.ParentId.Value)))
            {
                result.Add(product.Id);
                continue;
            }

            // Vendor scope — matched by name (see ResolveAsync's own
            // comment on why: Vendor.cs has no real FK to products).
            if (vendorScopedRules.Count > 0)
            {
                var vendorName = ProductService.ExtractVendorName(product.Additional, "en");
                if (vendorName == null && product.ParentId != null &&
                    parentAdditionalById.TryGetValue(product.ParentId.Value, out var parentAdditional))
                    vendorName = ProductService.ExtractVendorName(parentAdditional, "en");

                if (vendorName != null)
                {
                    var matchedVendorRule = vendorScopedRules.FirstOrDefault(r =>
                        vendorNamesById.TryGetValue(r.VendorId!.Value, out var vn) &&
                        string.Equals(vn, vendorName, StringComparison.OrdinalIgnoreCase));
                    if (matchedVendorRule != null)
                    {
                        var catIds = vendorCatFiltersByRule.GetValueOrDefault(matchedVendorRule.Id, new List<int>());
                        var prodIds = vendorProdFiltersByRule.GetValueOrDefault(matchedVendorRule.Id, new List<int>());

                        if (catIds.Count == 0 && prodIds.Count == 0)
                        {
                            result.Add(product.Id);
                            continue;
                        }

                        var matchesProduct = prodIds.Contains(product.Id) ||
                            (product.ParentId != null && prodIds.Contains(product.ParentId.Value));
                        var matchesCategory = false;
                        var productCategoryIds = product.Categories.Select(c => c.Id).ToHashSet();
                        foreach (var catId in catIds)
                        {
                            if ((await SubtreeOf(catId)).Any(id => productCategoryIds.Contains(id)))
                            {
                                matchesCategory = true;
                                break;
                            }
                        }
                        if (matchesProduct || matchesCategory)
                        {
                            result.Add(product.Id);
                            continue;
                        }
                    }
                }
            }

            // Category scope (subtree-inclusive).
            if (categoryScopedRules.Count > 0)
            {
                var productCategoryIds = product.Categories.Select(c => c.Id).ToHashSet();
                var matchedCategory = false;
                foreach (var rule in categoryScopedRules)
                {
                    if ((await SubtreeOf(rule.CategoryId!.Value)).Any(id => productCategoryIds.Contains(id)))
                    {
                        matchedCategory = true;
                        break;
                    }
                }
                if (matchedCategory)
                {
                    result.Add(product.Id);
                    continue;
                }
            }

            // Location scope — same rule applies (or doesn't) to every
            // product once a pincode matches.
            if (locationRule != null) result.Add(product.Id);
        }

        return result;
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
