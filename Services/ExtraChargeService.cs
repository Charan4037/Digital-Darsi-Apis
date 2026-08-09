using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Cart;

namespace DOSApi.Services;

/// <summary>A single resolved charge line, ready to display or persist —
/// Amount is always the actual rupee value, whether the charge itself is
/// "fixed" or "percentage", and whether it's cart-wide or category-scoped.</summary>
public record ExtraChargeLine(string Name, string ChargeType, decimal Rate, decimal Amount);

/// <summary>
/// Resolves the admin-managed extra-charge list (Handling Charges,
/// Processing Fee, etc. — see ExtraCharge / AdminExtraChargesController)
/// against a cart's items.
///
/// A charge with no CategoryId applies to every cart unconditionally, same
/// as before — "fixed" is a flat rupee amount, "percentage" is against the
/// whole cart subtotal. Global charges are unaffected by category-scoped
/// resolution below and are always summed in full.
///
/// A charge WITH a CategoryId only applies when the cart contains at least
/// one item from that category or any of its subcategories (resolved via
/// ProductService.GetSubtreeCategoryIdsAsync — picking a parent category
/// covers everything filed under it, same convention used everywhere else
/// category-scoping happens in this codebase). For those:
///   - "fixed" applies once per cart (not once per matching item/qty).
///   - "percentage" is computed against only the matching items' subtotal,
///     not the whole cart — a "2% Electronics handling fee" on a cart with
///     ₹1000 of Electronics in a ₹5000 cart is ₹20, not ₹100.
///
/// When a cart spans MULTIPLE categories that each have their own
/// category-scoped charge, only the single HIGHEST-resolved one is added —
/// not the sum of all of them. A cart with both a "Fragile Handling Fee"
/// (₹50) and an "Electronics Fee" (₹30) item never gets charged ₹80, only
/// the ₹50 fee. This "highest wins" rule applies only among category-scoped
/// charges; global (cart-wide) charges always apply on top, in full.
/// </summary>
public class ExtraChargeService
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    public ExtraChargeService(DOSDbContext db, ProductService productService)
    {
        _db = db;
        _productService = productService;
    }

    public async Task<(List<ExtraChargeLine> lines, decimal total)> ComputeAsync(List<CartItem> items)
    {
        var charges = await _db.ExtraCharges
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Id)
            .ToListAsync();

        var cartSubtotal = items.Sum(i => i.Total);
        var lines = new List<ExtraChargeLine>();
        var categoryLines = new List<ExtraChargeLine>();
        decimal total = 0;

        // Cache subtree lookups — multiple charges can target the same
        // category, and this is called on every cart mutation.
        var subtreeCache = new Dictionary<int, HashSet<int>>();

        foreach (var c in charges)
        {
            if (c.CategoryId == null)
            {
                var amount = c.ChargeType == "percentage"
                    ? Round(cartSubtotal * c.Amount / 100m)
                    : c.Amount;
                lines.Add(new ExtraChargeLine(c.Name, c.ChargeType, c.Amount, amount));
                total += amount;
                continue;
            }

            if (!subtreeCache.TryGetValue(c.CategoryId.Value, out var subtreeIds))
            {
                var ids = await _productService.GetSubtreeCategoryIdsAsync(c.CategoryId.Value);
                subtreeIds = ids.ToHashSet();
                subtreeCache[c.CategoryId.Value] = subtreeIds;
            }

            var matchingItems = items.Where(i => ItemInCategories(i, subtreeIds)).ToList();
            if (matchingItems.Count == 0) continue;

            var categorySubtotal = matchingItems.Sum(i => i.Total);
            var matchedAmount = c.ChargeType == "percentage"
                ? Round(categorySubtotal * c.Amount / 100m)
                : c.Amount;

            categoryLines.Add(new ExtraChargeLine(c.Name, c.ChargeType, c.Amount, matchedAmount));
        }

        // Category-scoped charges: only the highest-resolved one is charged,
        // regardless of how many distinct categories in the cart have their
        // own charge configured — see the "highest wins" note above.
        if (categoryLines.Count > 0)
        {
            var highest = categoryLines.OrderByDescending(l => l.Amount).First();
            lines.Add(highest);
            total += highest.Amount;
        }

        return (lines, total);
    }

    /// <summary>A cart item's own product may carry the category assignment,
    /// or (for variant/child products) its parent might — check both, same
    /// fallback pattern used for images/other display fields elsewhere.</summary>
    private static bool ItemInCategories(CartItem item, HashSet<int> subtreeIds)
    {
        var own = item.Product?.Categories;
        if (own != null && own.Any(cat => subtreeIds.Contains(cat.Id))) return true;

        var parent = item.Product?.Parent?.Categories;
        if (parent != null && parent.Any(cat => subtreeIds.Contains(cat.Id))) return true;

        return false;
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
