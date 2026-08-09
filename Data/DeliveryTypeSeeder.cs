using Microsoft.EntityFrameworkCore;
using DOSApi.Models;

namespace DOSApi.Data;

public static class DeliveryTypeSeeder
{
    public static async Task EnsureTableAndSeedAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations at
        // startup, so any new table must be created defensively.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS delivery_types (
                id             INT AUTO_INCREMENT PRIMARY KEY,
                code           VARCHAR(64)  NOT NULL UNIQUE,
                name           VARCHAR(128) NOT NULL,
                description    VARCHAR(255) NULL,
                price          DECIMAL(12,4) NOT NULL DEFAULT 0,
                delivery_hours INT          NOT NULL DEFAULT 24,
                sort_order     INT          NOT NULL DEFAULT 0,
                is_active      TINYINT(1)   NOT NULL DEFAULT 1,
                created_at     DATETIME     NULL,
                updated_at     DATETIME     NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Per-category price overrides for a tier (e.g. "Express" costs more
        // for a "Fragile" category) — admin-managed, no default rows, same
        // convention as extra_charges (see ExtraChargeSeeder).
        const string createCategoryPricesTableSql = @"
            CREATE TABLE IF NOT EXISTS delivery_type_category_prices (
                id                INT AUTO_INCREMENT PRIMARY KEY,
                delivery_type_id  INT           NOT NULL,
                category_id       INT           NOT NULL,
                price             DECIMAL(12,4) NOT NULL DEFAULT 0,
                is_active         TINYINT(1)    NOT NULL DEFAULT 1,
                created_at        DATETIME      NULL,
                updated_at        DATETIME      NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createCategoryPricesTableSql);

        if (await db.DeliveryTypes.AnyAsync())
        {
            Console.WriteLine("[DeliveryTypeSeeder] delivery_types already seeded. Skipping.");
            return;
        }

        var now = DateTime.UtcNow;
        db.DeliveryTypes.AddRange(
            new DeliveryType
            {
                Code = "express",
                Name = "Express Delivery",
                Description = "Orders Deliver in 2 hours",
                Price = 20m,
                DeliveryHours = 2,
                SortOrder = 1,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new DeliveryType
            {
                Code = "normal",
                Name = "Normal Delivery",
                Description = "Order Deliver in 6 hours",
                Price = 10m,
                DeliveryHours = 6,
                SortOrder = 2,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new DeliveryType
            {
                Code = "free",
                Name = "Free Delivery",
                Description = "Order Will Deliver in 24 Hours",
                Price = 0m,
                DeliveryHours = 24,
                SortOrder = 3,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            }
        );

        await db.SaveChangesAsync();
        Console.WriteLine("[DeliveryTypeSeeder] Seeded 3 delivery types.");
    }
}
