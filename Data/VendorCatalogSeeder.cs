using Microsoft.EntityFrameworkCore;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Data;

/// <summary>
/// Backfills the `vendors` table from the free-text seller names already
/// present in the product catalog (`products.additional` JSON, key
/// `vendor_en`/`vendor_te` — see ProductService.ExtractVendorName). Runs on
/// every startup and only inserts names not already present, so it keeps
/// picking up new vendor names as new products get added — this is
/// intentionally NOT a one-shot "skip if already seeded" seeder like
/// DeliveryTypeSeeder, since the source data (the catalog) keeps growing.
/// </summary>
public static class VendorCatalogSeeder
{
    public static async Task EnsureTableAndSeedAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations
        // at startup, so any new table must be created defensively.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS vendors (
                id         INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                name       VARCHAR(255) NOT NULL,
                active     TINYINT(1)   NOT NULL DEFAULT 1,
                created_at DATETIME     NULL,
                updated_at DATETIME     NULL,
                UNIQUE KEY UX_vendors_name (name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Minimal projection — avoids materializing full Product entities
        // (with their Flats/Inventories/Categories navigation props) just to
        // read one JSON column across ~3000 rows.
        var additionalJsonValues = await db.Products
            .Where(p => p.ParentId == null && p.Additional != null && p.Additional != "")
            .Select(p => p.Additional)
            .ToListAsync();

        var distinctNames = additionalJsonValues
            .Select(json => ProductService.ExtractVendorName(json, "en")?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingNames = (await db.Vendors.Select(v => v.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newNames = distinctNames.Where(name => !existingNames.Contains(name)).ToList();
        if (newNames.Count == 0)
        {
            Console.WriteLine($"[VendorCatalogSeeder] {existingNames.Count} vendors already seeded, none new.");
            return;
        }

        var now = DateTime.UtcNow;
        db.Vendors.AddRange(newNames.Select(name => new Vendor
        {
            Name = name,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now
        }));
        await db.SaveChangesAsync();
        Console.WriteLine($"[VendorCatalogSeeder] Seeded {newNames.Count} new vendor(s) (total {existingNames.Count + newNames.Count}).");
    }
}
