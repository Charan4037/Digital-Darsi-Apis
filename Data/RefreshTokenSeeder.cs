using Microsoft.EntityFrameworkCore;

namespace BagistoApi.Data;

/// <summary>
/// Ensures the <c>customer_refresh_tokens</c> table exists. Runs at startup
/// because this codebase doesn't run EF migrations — every new table must
/// self-provision via raw SQL the same way <see cref="DeliveryTypeSeeder"/>
/// does. Safe to call on every boot (CREATE TABLE IF NOT EXISTS).
/// </summary>
public static class RefreshTokenSeeder
{
    public static async Task EnsureTableAsync(BagistoDbContext db)
    {
        // customer_id MUST match the exact data type of customers.id. In
        // this Bagisto schema that's INT UNSIGNED (verified via SHOW CREATE
        // TABLE customers — Laravel's `increments` not `bigIncrements`).
        // MySQL FKs are strict about type AND signedness — INT vs INT
        // UNSIGNED is enough to fail with "incompatible" and roll back
        // the whole CREATE TABLE. Match exactly.
        //
        // Note: the EF model uses `int CustomerId`; MySqlConnector handles
        // INT UNSIGNED ↔ int as long as values fit in 32-bit signed range,
        // which all real customer IDs do (max ~2.1B).
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS customer_refresh_tokens (
                id                BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                customer_id       INT UNSIGNED NOT NULL,
                token_hash        VARCHAR(128) NOT NULL,
                expires_at        DATETIME     NOT NULL,
                created_at        DATETIME     NOT NULL,
                revoked_at        DATETIME     NULL,
                replaced_by_hash  VARCHAR(128) NULL,
                created_ip        VARCHAR(64)  NULL,
                user_agent        VARCHAR(512) NULL,
                UNIQUE KEY ux_customer_refresh_token_hash (token_hash),
                KEY ix_customer_refresh_customer (customer_id),
                CONSTRAINT fk_customer_refresh_customer
                    FOREIGN KEY (customer_id) REFERENCES customers(id)
                    ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Best-effort cleanup of expired+revoked rows older than 90 days so
        // the table doesn't grow unbounded. Failure here is non-fatal.
        try
        {
            const string pruneSql = @"
                DELETE FROM customer_refresh_tokens
                WHERE (revoked_at IS NOT NULL AND revoked_at < (UTC_TIMESTAMP() - INTERVAL 90 DAY))
                   OR (expires_at < (UTC_TIMESTAMP() - INTERVAL 90 DAY));
            ";
            await db.Database.ExecuteSqlRawAsync(pruneSql);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RefreshTokenSeeder] Prune skipped: {ex.Message}");
        }
    }
}
