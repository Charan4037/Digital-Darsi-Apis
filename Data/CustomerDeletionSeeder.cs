using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Ensures customers.is_deleted / customers.deleted_at exist. This codebase
/// doesn't run EF migrations — every new column self-provisions via raw SQL
/// the same way <see cref="NotificationSeeder"/> does. MySQL older than
/// 8.0.29 doesn't support ADD COLUMN IF NOT EXISTS, so each ALTER is
/// attempted and a "duplicate column" error (1060) is swallowed on repeat
/// boots.
/// </summary>
public static class CustomerDeletionSeeder
{
    public static async Task EnsureColumnsAsync(DOSDbContext db)
    {
        var newColumns = new (string Name, string Ddl)[]
        {
            ("is_deleted", "ALTER TABLE customers ADD COLUMN is_deleted TINYINT(1) NOT NULL DEFAULT 0"),
            ("deleted_at", "ALTER TABLE customers ADD COLUMN deleted_at DATETIME NULL"),
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
                Console.WriteLine($"[CustomerDeletionSeeder] Could not add column {name}: {ex.Message}");
            }
        }
    }
}
