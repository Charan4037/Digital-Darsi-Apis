using Microsoft.EntityFrameworkCore;

namespace DOSApi.Data;

public static class BannerSeeder
{
    public static async Task EnsureTableAndSeedAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations at
        // startup, so any new table must be created defensively. The table is
        // normally created ad hoc by the scraper (scrape_banners.py), so a
        // fresh install may not have it yet.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS dd_scraped_banners (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                site_key    VARCHAR(32)   NOT NULL,
                image_url   VARCHAR(500)  NOT NULL,
                title       VARCHAR(255)  NULL,
                subtitle    VARCHAR(255)  NULL,
                link_url    VARCHAR(500)  NULL,
                link_type   VARCHAR(20)   NULL,
                category_id INT           NULL,
                product_id  INT           NULL,
                vendor_id   INT           NULL,
                sort_order  INT           NOT NULL DEFAULT 0
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        // Existing installs (table already created by the scraper, or before
        // link_type/category_id/product_id existed) — add the new columns
        // defensively, one statement at a time so an already-present column
        // doesn't abort the ones after it.
        var alterStatements = new[]
        {
            "ALTER TABLE dd_scraped_banners ADD COLUMN link_type VARCHAR(20) NULL",
            "ALTER TABLE dd_scraped_banners ADD COLUMN category_id INT NULL",
            "ALTER TABLE dd_scraped_banners ADD COLUMN product_id INT NULL",
            "ALTER TABLE dd_scraped_banners ADD COLUMN vendor_id INT NULL",
        };
        foreach (var sql in alterStatements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
            {
                // Duplicate column — already present from a previous boot.
            }
        }
    }
}
