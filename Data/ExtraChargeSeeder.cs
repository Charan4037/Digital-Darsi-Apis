using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

public static class ExtraChargeSeeder
{
    public static async Task EnsureTableAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations at
        // startup, so any new table must be created defensively. No default
        // rows: admin adds whatever charges are needed (free text name +
        // fixed/percentage amount) via AdminExtraChargesController.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS extra_charges (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                name        VARCHAR(128) NOT NULL,
                charge_type VARCHAR(16)  NOT NULL DEFAULT 'fixed',
                amount      DECIMAL(12,2) NOT NULL DEFAULT 0,
                sort_order  INT          NOT NULL DEFAULT 0,
                is_active   TINYINT(1)   NOT NULL DEFAULT 1,
                created_at  DATETIME     NULL,
                updated_at  DATETIME     NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // category_id predates the category-scoped charges feature — add it
        // defensively. NULL means "applies to every cart", same as before.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE extra_charges ADD COLUMN category_id INT NULL");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }

        // orders.extra_charges_total predates this feature — add it
        // defensively so PlaceOrderAsync can persist what was actually
        // charged, for audit/refund correctness even though there's no
        // itemized order-history UI for it yet.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE orders ADD COLUMN extra_charges_total DECIMAL(12,4) NULL DEFAULT 0");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }
    }
}
