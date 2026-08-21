using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Helpers;
using DOSApi.Models.Cart;

namespace DOSApi.Services;

/// <summary>
/// Resolves the admin-managed delivery tiers (DeliveryType — Express/Normal/
/// Free, or whatever the admin has configured) against a cart's items,
/// applying each tier's per-category price overrides
/// (DeliveryTypeCategoryPrice) when present.
///
/// A tier with no overrides always resolves to its global default Price.
/// A tier WITH overrides resolves, per cart item, to the override price for
/// whichever category (or subtree — see ProductService.GetSubtreeCategoryIdsAsync)
/// that item belongs to, or the tier's global default if the item matches no
/// override. The tier's final price for the cart is the MAX of those
/// per-item effective rates — so a cart spanning several categories is never
/// undercharged for the more expensive-to-ship one.
/// </summary>
public class DeliveryChargeService
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    public DeliveryChargeService(DOSDbContext db, ProductService productService)
    {
        _db = db;
        _productService = productService;
    }

    public async Task<List<ShippingRateDto>> ResolveRatesAsync(List<CartItem> items)
    {
        var tiers = await _db.DeliveryTypes
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Id)
            .ToListAsync();

        if (tiers.Count == 0) return new List<ShippingRateDto>();

        var tierIds = tiers.Select(t => t.Id).ToList();
        var overrides = await _db.DeliveryTypeCategoryPrices
            .AsNoTracking()
            .Where(o => o.IsActive && tierIds.Contains(o.DeliveryTypeId))
            .ToListAsync();

        // Cache subtree lookups — multiple tiers/overrides can target the
        // same category, and this runs on every rate fetch.
        var subtreeCache = new Dictionary<int, HashSet<int>>();

        var result = new List<ShippingRateDto>();
        foreach (var tier in tiers)
        {
            var tierOverrides = overrides.Where(o => o.DeliveryTypeId == tier.Id).ToList();
            var resolvedPrice = tier.Price;

            if (tierOverrides.Count > 0 && items.Count > 0)
            {
                var maxRate = tier.Price;
                foreach (var item in items)
                {
                    var matchingPrices = new List<decimal>();
                    foreach (var ov in tierOverrides)
                    {
                        if (!subtreeCache.TryGetValue(ov.CategoryId, out var subtreeIds))
                        {
                            var ids = await _productService.GetSubtreeCategoryIdsAsync(ov.CategoryId);
                            subtreeIds = ids.ToHashSet();
                            subtreeCache[ov.CategoryId] = subtreeIds;
                        }
                        if (ItemInCategories(item, subtreeIds))
                            matchingPrices.Add(ov.Price);
                    }

                    var effectiveRate = matchingPrices.Count > 0 ? matchingPrices.Max() : tier.Price;
                    if (effectiveRate > maxRate) maxRate = effectiveRate;
                }
                resolvedPrice = maxRate;
            }

            result.Add(new ShippingRateDto
            {
                Id = tier.Id,
                Code = $"{tier.Code}_{tier.Code}",
                Label = tier.Name,
                Description = tier.Description,
                Method = $"{tier.Code}_{tier.Code}",
                MethodTitle = tier.Name,
                Price = resolvedPrice,
                FormattedPrice = PriceFormatter.Format(resolvedPrice),
                BasePrice = resolvedPrice,
                BaseFormattedPrice = PriceFormatter.Format(resolvedPrice),
                Carrier = tier.Code,
                CarrierTitle = tier.Name,
                DeliveryHours = tier.DeliveryHours
            });
        }

        return result;
    }

    /// <summary>A cart item's own product may carry the category assignment,
    /// or (for variant/child products) its parent might — check both, same
    /// fallback pattern ExtraChargeService uses.</summary>
    private static bool ItemInCategories(CartItem item, HashSet<int> subtreeIds)
    {
        var own = item.Product?.Categories;
        if (own != null && own.Any(cat => subtreeIds.Contains(cat.Id))) return true;

        var parent = item.Product?.Parent?.Categories;
        if (parent != null && parent.Any(cat => subtreeIds.Contains(cat.Id))) return true;

        return false;
    }
}
