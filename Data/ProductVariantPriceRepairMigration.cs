using Microsoft.EntityFrameworkCore;
using DOSApi.Models.Catalog;

namespace DOSApi.Data;

/// <summary>
/// Fix for products where parent price matches a child price (indicating incorrect update).
/// Sets parent price to the minimum child price when this condition is detected.
/// This is a one-time repair for the 423 affected products in the current database.
/// </summary>
public static class ProductVariantPriceRepairMigration
{
    public static async Task RepairAllProductsAsync(DOSDbContext db)
    {
        // Get all products with children (DO NOT use AsNoTracking for write operations!)
        var productsWithChildren = await db.Products
            .Where(p => p.Children.Any())
            .Include(p => p.Children)
                .ThenInclude(c => c.Flats)
            .Include(p => p.Flats)
            .ToListAsync();

        int repaired = 0;

        foreach (var parent in productsWithChildren)
        {
            if (!parent.Children.Any()) continue;

            // Get all child prices
            var childPrices = parent.Children
                .Select(c => c.Flats.FirstOrDefault()?.Price)
                .Where(p => p.HasValue && p.Value > 0)
                .Select(p => p.Value)
                .ToList();

            if (!childPrices.Any()) continue;

            var minChildPrice = childPrices.Min();
            var parentFlat = parent.Flats.FirstOrDefault();

            // Only repair if parent price matches one of the child prices
            // (indicates it was incorrectly set to a child price)
            if (parentFlat != null && parentFlat.Price.HasValue && childPrices.Contains(parentFlat.Price.Value))
            {
                // Set parent to minimum child price as default
                parentFlat.Price = minChildPrice;
                db.ProductFlats.Update(parentFlat);
                repaired++;
            }
        }

        if (repaired > 0)
        {
            await db.SaveChangesAsync();
            Console.WriteLine($"[ProductVariantPriceRepair] Repaired {repaired} products");
        }
    }
}
