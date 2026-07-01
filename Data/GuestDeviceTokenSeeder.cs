using Microsoft.EntityFrameworkCore;

namespace BagistoApi.Data;

/// <summary>
/// Ensures the <c>guest_device_tokens</c> table exists. Follows the same
/// self-provisioning pattern as <see cref="DeviceTokenSeeder"/> — no EF
/// migrations; CREATE TABLE IF NOT EXISTS on every startup.
/// </summary>
public static class GuestDeviceTokenSeeder
{
    public static async Task EnsureTableAsync(BagistoDbContext db)
    {
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS guest_device_tokens (
                id             BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                session_token  VARCHAR(512) NOT NULL,
                fcm_token      VARCHAR(512) NOT NULL,
                platform       VARCHAR(16)  NULL,
                device_id      VARCHAR(128) NULL,
                app_version    VARCHAR(32)  NULL,
                device_model   VARCHAR(128) NULL,
                expires_at     DATETIME     NOT NULL,
                created_at     DATETIME     NOT NULL,
                updated_at     DATETIME     NOT NULL,
                UNIQUE KEY ux_guest_device_session (session_token(191)),
                KEY ix_guest_device_fcm (fcm_token(191))
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);
    }
}
