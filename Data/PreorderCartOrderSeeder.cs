using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Defensive column additions wiring Preorder into Cart/Order — this
/// codebase doesn't run EF migrations at startup, so new columns on
/// pre-existing tables must be added defensively, same pattern as
/// PaymentSettingsSeeder. No new tables here (preorder_rules/preorder_slots
/// already exist — see PreorderSeeder).
/// </summary>
public static class PreorderCartOrderSeeder
{
    public static async Task EnsureSchemaAsync(DOSDbContext db)
    {
        // cart.preorder_delivery_date/preorder_slot_id are legacy — superseded
        // by the cart_preorder_selections table below (a cart can now have a
        // pending selection per distinct preorder rule, not just one for the
        // whole cart). Left in place, unused by any new code, rather than
        // dropped, to avoid a destructive migration.
        await AddColumnIfMissingAsync(db, "cart", "preorder_delivery_date", "DATETIME NULL");
        await AddColumnIfMissingAsync(db, "cart", "preorder_slot_id", "INT NULL");
        await AddColumnIfMissingAsync(db, "cart", "preorder_phase_completed_at", "DATETIME NULL");

        await AddColumnIfMissingAsync(db, "orders", "is_preorder", "TINYINT(1) NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "orders", "preorder_delivery_date", "DATETIME NULL");
        await AddColumnIfMissingAsync(db, "orders", "preorder_slot_id", "INT NULL");
        await AddColumnIfMissingAsync(db, "orders", "preorder_slot_label", "VARCHAR(64) NULL");
        await AddColumnIfMissingAsync(db, "orders", "preorder_rule_id", "INT NULL");

        await AddColumnIfMissingAsync(db, "preorder_rules", "min_lead_hours", "INT NULL");

        await AddColumnIfMissingAsync(db, "cart_payment", "is_preorder_payment", "TINYINT(1) NOT NULL DEFAULT 0");

        // One pending delivery-slot selection per (cart, preorder rule) —
        // replaces the single cart.preorder_delivery_date/preorder_slot_id
        // pair now that a cart can have several distinct preorder groups at
        // once. Consumed (row deleted) once that group's order is placed.
        const string createSelectionsTableSql = @"
            CREATE TABLE IF NOT EXISTS cart_preorder_selections (
                id                INT AUTO_INCREMENT PRIMARY KEY,
                cart_id           INT      NOT NULL,
                preorder_rule_id  INT      NOT NULL,
                delivery_date     DATE     NOT NULL,
                slot_id           INT      NOT NULL,
                created_at        DATETIME NULL,
                updated_at        DATETIME NULL,
                UNIQUE KEY uq_cart_preorder_rule (cart_id, preorder_rule_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createSelectionsTableSql);
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
