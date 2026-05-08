using Microsoft.EntityFrameworkCore;

namespace BagistoApi.Data;

/// <summary>
/// Ensures the <c>customer_device_tokens</c> table exists. Mirrors the
/// pattern used by <see cref="RefreshTokenSeeder"/> — this codebase doesn't
/// run EF migrations, so each new table self-provisions on startup via
/// CREATE TABLE IF NOT EXISTS.
/// </summary>
public static class DeviceTokenSeeder
{
    public static async Task EnsureTableAsync(BagistoDbContext db)
    {
        // customer_id must match customers.id exactly — INT UNSIGNED in this
        // Bagisto schema (Laravel `increments` not `bigIncrements`). MySQL
        // FKs are strict about signedness, so don't try BIGINT here.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS customer_device_tokens (
                id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                customer_id   INT UNSIGNED NOT NULL,
                fcm_token     VARCHAR(512) NOT NULL,
                platform      VARCHAR(16)  NULL,
                device_id     VARCHAR(128) NULL,
                app_version   VARCHAR(32)  NULL,
                last_seen_at  DATETIME     NULL,
                created_at    DATETIME     NOT NULL,
                updated_at    DATETIME     NOT NULL,
                UNIQUE KEY ux_customer_device_token (fcm_token),
                KEY ix_customer_device_customer (customer_id),
                CONSTRAINT fk_customer_device_customer
                    FOREIGN KEY (customer_id) REFERENCES customers(id)
                    ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);
    }
}
