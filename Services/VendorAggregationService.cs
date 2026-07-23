using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Services;

public record VendorAggregate(int Products, int Orders, decimal Revenue);

/// <summary>
/// Computes per-vendor product/order/revenue aggregates by matching the
/// free-text vendor name in each product's `additional` JSON
/// (vendor_en/vendor_te — see ProductService.ExtractVendorName) against
/// order items. There is no FK from products/orders to a vendor, so this
/// is done in-memory per call. Cheap at current catalog scale (~3000
/// products); revisit with a materialized/cached aggregate if that grows
/// much larger. Shared by AdminDashboardController and AdminVendorsController
/// so both compute vendor stats the same way.
/// </summary>
public class VendorAggregationService
{
    private readonly DOSDbContext _db;

    public VendorAggregationService(DOSDbContext db)
    {
        _db = db;
    }

    public async Task<Dictionary<int, string>> BuildProductVendorMapAsync()
    {
        var rows = await _db.Products
            .Where(p => p.ParentId == null && p.Additional != null && p.Additional != "")
            .Select(p => new { p.Id, p.Additional })
            .ToListAsync();

        var map = new Dictionary<int, string>();
        foreach (var r in rows)
        {
            var name = ProductService.ExtractVendorName(r.Additional, "en")?.Trim();
            if (!string.IsNullOrWhiteSpace(name)) map[r.Id] = name;
        }
        return map;
    }

    public async Task<Dictionary<string, VendorAggregate>> BuildVendorAggregatesAsync(
        Dictionary<int, string> productVendorMap)
    {
        var productCounts = productVendorMap.Values
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var items = await _db.OrderItems
            .Where(i => i.ProductId != null && i.OrderId != null)
            .Select(i => new { i.OrderId, i.ProductId, i.Price, i.QtyOrdered })
            .ToListAsync();

        var orderIdsByVendor = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var revenueByVendor = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!productVendorMap.TryGetValue(item.ProductId!.Value, out var vendorName)) continue;
            if (!orderIdsByVendor.TryGetValue(vendorName, out var set))
            {
                set = new HashSet<int>();
                orderIdsByVendor[vendorName] = set;
            }
            set.Add(item.OrderId!.Value);
            revenueByVendor[vendorName] = revenueByVendor.GetValueOrDefault(vendorName)
                + (item.Price ?? 0) * (item.QtyOrdered ?? 0);
        }

        var names = productCounts.Keys
            .Concat(orderIdsByVendor.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, VendorAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            result[name] = new VendorAggregate(
                Products: productCounts.GetValueOrDefault(name),
                Orders: orderIdsByVendor.TryGetValue(name, out var set) ? set.Count : 0,
                Revenue: revenueByVendor.GetValueOrDefault(name));
        }
        return result;
    }
}
