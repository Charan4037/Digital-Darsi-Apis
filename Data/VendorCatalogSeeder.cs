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
                name_te    VARCHAR(255) NULL,
                active     TINYINT(1)   NOT NULL DEFAULT 1,
                created_at DATETIME     NULL,
                updated_at DATETIME     NULL,
                UNIQUE KEY UX_vendors_name (name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Backfill on pre-existing installations (this table predates the
        // Telugu-name feature). MySQL < 8.0.29 doesn't support ADD COLUMN IF
        // NOT EXISTS, so attempt the ALTER and swallow "duplicate column" —
        // same pattern as DeviceTokenSeeder.
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE vendors ADD COLUMN name_te VARCHAR(255) NULL");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VendorCatalogSeeder] Could not add column name_te: {ex.Message}");
        }

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

        // First real (non-mirrored) scraped Telugu name seen per English name —
        // scraped product rows often already carry a genuine vendor_te distinct
        // from vendor_en, predating the `vendors.name_te` column entirely. Used
        // below to backfill any vendor that doesn't have a Telugu name yet,
        // instead of leaving it null until someone edits the vendor by hand.
        var teByEnglishName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in additionalJsonValues)
        {
            var en = ProductService.ExtractVendorName(json, "en")?.Trim();
            var te = ProductService.ExtractVendorName(json, "te")?.Trim();
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(te)) continue;
            if (string.Equals(en, te, StringComparison.OrdinalIgnoreCase)) continue; // not a real translation
            teByEnglishName.TryAdd(en, te);
        }

        var existingVendors = await db.Vendors.ToListAsync();
        var existingNames = existingVendors.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var changed = false;

        var newNames = distinctNames.Where(name => !existingNames.Contains(name)).ToList();
        if (newNames.Count > 0)
        {
            db.Vendors.AddRange(newNames.Select(name => new Vendor
            {
                Name = name,
                NameTe = teByEnglishName.TryGetValue(name, out var te) ? te : null,
                Active = true,
                CreatedAt = now,
                UpdatedAt = now
            }));
            changed = true;
            Console.WriteLine($"[VendorCatalogSeeder] Seeded {newNames.Count} new vendor(s) (total {existingNames.Count + newNames.Count}).");
        }

        var backfilled = 0;
        foreach (var v in existingVendors)
        {
            if (string.IsNullOrWhiteSpace(v.NameTe) && teByEnglishName.TryGetValue(v.Name, out var te))
            {
                v.NameTe = te;
                v.UpdatedAt = now;
                backfilled++;
            }
        }
        if (backfilled > 0)
        {
            changed = true;
            Console.WriteLine($"[VendorCatalogSeeder] Backfilled Telugu name for {backfilled} existing vendor(s) from scraped product data.");
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
        else
        {
            Console.WriteLine($"[VendorCatalogSeeder] {existingNames.Count} vendors already seeded, none new.");
        }
    }
}
