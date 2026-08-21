using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Catalog;

namespace DOSApi.Services;

/// <summary>
/// Resolves whether a product is currently under preorder and, if so, which
/// PreorderRule governs it. Used today by AdminPreorderController's
/// "resolve" preview endpoint so an admin can verify targeting before any
/// consumer-facing wiring exists; the same method will back the cart/
/// checkout preorder check once that's built.
/// </summary>
public class PreorderService
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    /// <summary>Used when a matching rule's WindowDays is null.</summary>
    public const int DefaultWindowDays = 6;

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
