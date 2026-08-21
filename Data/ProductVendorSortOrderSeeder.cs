using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Creates the `product_vendor_sort_orders` table used to rank a vendor's
/// own products against each other (see AdminVendorsController.ReorderProducts
/// and CategoryController.QueryCategoryProductsAsync). No seed data — rows
/// only appear once an admin explicitly reorders a vendor's products.
/// </summary>
public static class ProductVendorSortOrderSeeder
{
    public static async Task EnsureTableAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations
        // at startup, so any new table must be created defensively.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS product_vendor_sort_orders (
                product_id INT NOT NULL PRIMARY KEY,
                vendor_id  INT NOT NULL,
                sort_order INT NOT NULL DEFAULT 0,
                created_at DATETIME NULL,
                updated_at DATETIME NULL,
                KEY IX_product_vendor_sort_orders_vendor_id (vendor_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);
    }
}
