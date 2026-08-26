using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Creates the `vendor_category_sort_orders` table used to override a
/// vendor's home-page priority per category (see
/// Models/Catalog/VendorCategorySortOrder.cs and
/// CategoryController.QueryCategoryProductsAsync). No seed data — rows only
/// appear once an admin explicitly sets a category-scoped priority via
/// AdminVendorsController.
/// </summary>
public static class VendorCategorySortOrderSeeder
{
    public static async Task EnsureTableAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations
        // at startup, so any new table must be created defensively.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS vendor_category_sort_orders (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                vendor_id   INT          NOT NULL,
                category_id INT          NOT NULL,
                sort_order  INT          NOT NULL DEFAULT 0,
                group_id    VARCHAR(36)  NULL,
                is_active   TINYINT(1)   NOT NULL DEFAULT 1,
                created_at  DATETIME     NULL,
                updated_at  DATETIME     NULL,
                UNIQUE KEY UX_vendor_category_sort_orders (vendor_id, category_id),
                KEY IX_vendor_category_sort_orders_category_id (category_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);
    }
}
