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

        // Backfill on installs that already created the table before
        // image_url was added. MySQL < 8.0.29 doesn't support ADD COLUMN IF
        // NOT EXISTS, so attempt the ALTER and swallow "duplicate column" —
        // same pattern as VendorCatalogSeeder/DeviceTokenSeeder.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_records ADD COLUMN image_url VARCHAR(1024) NULL");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }
    }
}
