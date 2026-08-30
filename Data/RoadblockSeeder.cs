using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

/// <summary>
/// Table self-provisioning for <c>dd_roadblock_popups</c> — same pattern as
/// <see cref="BannerSeeder"/>, since this codebase doesn't run EF migrations
/// at startup.
/// </summary>
public static class RoadblockSeeder
{
    public static async Task EnsureTableAsync(DOSDbContext db)
    {
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS dd_roadblock_popups (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                image_url   VARCHAR(500)  NOT NULL,
                link_type   VARCHAR(20)   NULL,
                link_url    VARCHAR(500)  NULL,
                category_id INT           NULL,
                product_id  INT           NULL,
                vendor_id   INT           NULL,
                sort_order  INT           NOT NULL DEFAULT 0,
                is_active   TINYINT(1)    NOT NULL DEFAULT 1
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // The popup was originally per-store (site_key NOT NULL) before being
        // made global — drop that column defensively for any install that
        // already booted with the old schema. Swallows "can't drop, doesn't
        // exist" (1091) so a fresh install (which never had the column) or a
        // repeat boot (already dropped) doesn't fail.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dd_roadblock_popups DROP COLUMN site_key");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1091)
        {
            // Column already absent — nothing to do.
        }
    }
}
