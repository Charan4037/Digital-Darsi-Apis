using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Schema for multi-target preorder rules: preorder_rules.group_id lets
/// several rows (product- or category-scoped) be created together and
/// displayed as one card in the admin panel — same pattern as
/// delivery_type_category_prices.group_id (see DeliveryTypeSeeder).
/// preorder_rule_categories/preorder_rule_products are optional narrowing
/// filters owned by a single vendor-scoped rule (NOT the group-id pattern —
/// a vendor rule stays exactly one row; these are its attached filter rows).
/// All additive — a rule with group_id NULL and no filter rows resolves
/// exactly as it did before this feature existed.
/// </summary>
public static class PreorderScopeFilterSeeder
{
    public static async Task EnsureSchemaAsync(DOSDbContext db)
    {
        await AddColumnIfMissingAsync(db, "preorder_rules", "group_id", "VARCHAR(36) NULL");

        const string createRuleCategoriesSql = @"
            CREATE TABLE IF NOT EXISTS preorder_rule_categories (
                id                INT AUTO_INCREMENT PRIMARY KEY,
                preorder_rule_id  INT      NOT NULL,
                category_id       INT      NOT NULL,
                created_at        DATETIME NULL,
                FOREIGN KEY (preorder_rule_id) REFERENCES preorder_rules(id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createRuleCategoriesSql);

        const string createRuleProductsSql = @"
            CREATE TABLE IF NOT EXISTS preorder_rule_products (
                id                INT AUTO_INCREMENT PRIMARY KEY,
                preorder_rule_id  INT      NOT NULL,
                product_id        INT      NOT NULL,
                created_at        DATETIME NULL,
                FOREIGN KEY (preorder_rule_id) REFERENCES preorder_rules(id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createRuleProductsSql);
    }

    private static async Task AddColumnIfMissingAsync(DOSDbContext db, string table, string column, string definition)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }
    }
}
