using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Ensures the <c>notification_records</c> table exists. Mirrors the
/// pattern used by <see cref="DeviceTokenSeeder"/> — this codebase doesn't
/// run EF migrations, so each new table self-provisions on startup via
/// CREATE TABLE IF NOT EXISTS.
///
/// Named "notification_records", not "notifications" — see the comment on
/// Models/Customer/NotificationRecord.cs for why (Bagisto already owns a
/// `notifications` table with an incompatible schema).
/// </summary>
public static class NotificationSeeder
{
    public static async Task EnsureTableAsync(DOSDbContext db)
    {
        // customer_id nullable (broadcast rows) so no FK constraint here —
        // same call as CustomerAdmin.RoleId: a live production table with a
        // nullable FK is more risk than it's worth, and a dangling
        // customer_id (e.g. account later deleted) just stops matching any
        // customer's inbox query rather than breaking anything.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS notification_records (
                id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                customer_id   INT UNSIGNED NULL,
                title         VARCHAR(255) NOT NULL,
                body          VARCHAR(1024) NOT NULL,
                type          VARCHAR(64)  NULL,
                data_json     TEXT         NULL,
                image_url     VARCHAR(1024) NULL,
                created_at    DATETIME     NOT NULL,
                KEY ix_notification_records_customer_created (customer_id, created_at),
                KEY ix_notification_records_created (created_at)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Backfill on installs that already created the table before these
        // columns were added. MySQL < 8.0.29 doesn't support ADD COLUMN IF
        // NOT EXISTS, so attempt each ALTER and swallow "duplicate column" —
        // same pattern as VendorCatalogSeeder/DeviceTokenSeeder.
        var newColumns = new (string Name, string Ddl)[]
        {
            ("image_url", "ALTER TABLE notification_records ADD COLUMN image_url VARCHAR(1024) NULL"),
            ("is_read", "ALTER TABLE notification_records ADD COLUMN is_read TINYINT(1) NOT NULL DEFAULT 0"),
            ("read_at", "ALTER TABLE notification_records ADD COLUMN read_at DATETIME NULL"),
        };
        foreach (var (name, ddl) in newColumns)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl);
            }
            catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
            {
                // Duplicate column — already present from a previous boot.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationSeeder] Could not add column {name}: {ex.Message}");
            }
        }
    }
}
