using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Defensive column additions for Razorpay support — this codebase doesn't
/// run EF migrations at startup, so new columns on pre-existing tables must
/// be added defensively, same pattern as DeliveryTypeSeeder's group_id
/// backfill. No new tables: payment settings themselves live in the
/// pre-existing core_config table (see PaymentSettingsService).
/// </summary>
public static class PaymentSettingsSeeder
{
    public static async Task EnsureSchemaAsync(DOSDbContext db)
    {
        await AddColumnIfMissingAsync(db, "cart_payment", "razorpay_order_id", "VARCHAR(64) NULL");
        await AddColumnIfMissingAsync(db, "cart_payment", "razorpay_amount", "DECIMAL(12,4) NULL");
        await AddColumnIfMissingAsync(db, "order_payment", "transaction_id", "VARCHAR(128) NULL");
        await AddColumnIfMissingAsync(db, "order_payment", "is_verified", "TINYINT(1) NOT NULL DEFAULT 0");
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
