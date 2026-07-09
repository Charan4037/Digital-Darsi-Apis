using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Ensures the <c>customer_device_tokens</c> table exists. Mirrors the
/// pattern used by <see cref="RefreshTokenSeeder"/> — this codebase doesn't
/// run EF migrations, so each new table self-provisions on startup via
/// CREATE TABLE IF NOT EXISTS.
/// </summary>
public static class DeviceTokenSeeder
{
    public static async Task EnsureTableAsync(DOSDbContext db)
    {
        // customer_id must match customers.id exactly — INT UNSIGNED in this
        // DOS schema (Laravel `increments` not `bigIncrements`). MySQL
        // FKs are strict about signedness, so don't try BIGINT here.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS customer_device_tokens (
                id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                customer_id   INT UNSIGNED NOT NULL,
                fcm_token     VARCHAR(512) NOT NULL,
                platform      VARCHAR(16)  NULL,
                device_id     VARCHAR(128) NULL,
                app_version   VARCHAR(32)  NULL,
                build_number  VARCHAR(32)  NULL,
                device_model  VARCHAR(128) NULL,
                manufacturer  VARCHAR(64)  NULL,
                os_version    VARCHAR(64)  NULL,
                locale        VARCHAR(16)  NULL,
                timezone      VARCHAR(64)  NULL,
                last_seen_at  DATETIME     NULL,
                created_at    DATETIME     NOT NULL,
                updated_at    DATETIME     NOT NULL,
                UNIQUE KEY ux_customer_device_token (fcm_token(191)),
                KEY ix_customer_device_customer (customer_id),
                CONSTRAINT fk_customer_device_customer
                    FOREIGN KEY (customer_id) REFERENCES customers(id)
                    ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Backfill columns on pre-existing installations. MySQL < 8.0.29
        // does not support `ADD COLUMN IF NOT EXISTS`, so we attempt each
        // ALTER and swallow the 1060 "duplicate column" error. Anything
        // else gets logged but doesn't fail startup.
        var newColumns = new (string Name, string Ddl)[]
        {
            ("build_number", "ALTER TABLE customer_device_tokens ADD COLUMN build_number VARCHAR(32)  NULL"),
            ("device_model", "ALTER TABLE customer_device_tokens ADD COLUMN device_model VARCHAR(128) NULL"),
            ("manufacturer", "ALTER TABLE customer_device_tokens ADD COLUMN manufacturer VARCHAR(64)  NULL"),
            ("os_version",   "ALTER TABLE customer_device_tokens ADD COLUMN os_version   VARCHAR(64)  NULL"),
            ("locale",       "ALTER TABLE customer_device_tokens ADD COLUMN locale       VARCHAR(16)  NULL"),
            ("timezone",     "ALTER TABLE customer_device_tokens ADD COLUMN timezone     VARCHAR(64)  NULL"),
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
                Console.WriteLine($"[DeviceTokenSeeder] Could not add column {name}: {ex.Message}");
            }
        }
    }
}
