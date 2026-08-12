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

        // group_id powers the "scope to several categories at once" bulk-add
        // (see ExtraCharge.GroupId) — lets the admin list group same-batch
        // rows back into one card instead of N nearly-identical ones.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE extra_charges ADD COLUMN group_id VARCHAR(36) NULL");
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

        // orders.delivered_at powers the refund-window feature (see
        // AccountService.RequestRefundAsync) — set once, the first time an
        // order reaches "completed", by every status-transition endpoint
        // (AdminOrderController/AdminOrdersListController/VendorController).
        // Unrelated to extra charges, but this is where orders-table columns
        // already get added defensively at boot.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE orders ADD COLUMN delivered_at DATETIME NULL");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }

        // Itemized per-order snapshot of the charges above (see
        // Models/Sales/Order.cs — OrderExtraCharge) — orders.extra_charges_total
        // is just their sum. CheckoutService.PlaceOrderAsync writes to this
        // table on every order, so it must exist before the first checkout.
        const string createOrderExtraChargesSql = @"
            CREATE TABLE IF NOT EXISTS order_extra_charges (
                id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                order_id    INT UNSIGNED NOT NULL,
                name        VARCHAR(128) NOT NULL,
                charge_type VARCHAR(16)  NOT NULL DEFAULT 'fixed',
                rate        DECIMAL(12,4) NOT NULL DEFAULT 0,
                amount      DECIMAL(12,4) NOT NULL DEFAULT 0,
                sort_order  INT          NOT NULL DEFAULT 0,
                created_at  DATETIME     NULL,
                KEY IX_order_extra_charges_order_id (order_id),
                CONSTRAINT FK_order_extra_charges_orders FOREIGN KEY (order_id)
                    REFERENCES orders(id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createOrderExtraChargesSql);
    }
}
