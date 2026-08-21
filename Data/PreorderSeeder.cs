using Microsoft.EntityFrameworkCore;
using DOSApi.Models.Catalog;

namespace DOSApi.Data;

public static class PreorderSeeder
{
    public static async Task EnsureTableAndSeedAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations at
        // startup, so any new table must be created defensively.
        const string createRulesTableSql = @"
            CREATE TABLE IF NOT EXISTS preorder_rules (
                id           INT AUTO_INCREMENT PRIMARY KEY,
                scope_type   VARCHAR(16)  NOT NULL,
                product_id   INT          NULL,
                vendor_id    INT          NULL,
                category_id  INT          NULL,
                pincode      VARCHAR(16)  NULL,
                is_active    TINYINT(1)   NOT NULL DEFAULT 1,
                window_days  INT          NULL,
                note         VARCHAR(255) NULL,
                created_at   DATETIME     NULL,
                updated_at   DATETIME     NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createRulesTableSql);

        const string createSlotsTableSql = @"
            CREATE TABLE IF NOT EXISTS preorder_slots (
                id                INT AUTO_INCREMENT PRIMARY KEY,
                preorder_rule_id  INT          NOT NULL,
                label             VARCHAR(64)  NOT NULL,
                start_time        TIME         NOT NULL,
                end_time          TIME         NOT NULL,
                sort_order        INT          NOT NULL DEFAULT 0,
                is_active         TINYINT(1)   NOT NULL DEFAULT 1,
                created_at        DATETIME     NULL,
                updated_at        DATETIME     NULL,
                FOREIGN KEY (preorder_rule_id) REFERENCES preorder_rules(id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createSlotsTableSql);

        if (await db.PreorderRules.AnyAsync())
        {
            Console.WriteLine("[PreorderSeeder] preorder_rules already seeded. Skipping.");
            return;
        }

        // Seeded inactive — a starting point mirroring the "Change delivery
        // slot" screenshot's 3 time-of-day options, which the admin can
        // activate/edit rather than starting from a blank slate.
        var now = DateTime.UtcNow;
        var globalRule = new PreorderRule
        {
            ScopeType = "global",
            IsActive = false,
            WindowDays = 6,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.PreorderRules.Add(globalRule);
        await db.SaveChangesAsync();

        db.PreorderSlots.AddRange(
            new PreorderSlot
            {
                PreorderRuleId = globalRule.Id,
                Label = "9 AM - 12 PM",
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(12, 0, 0),
                SortOrder = 1,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PreorderSlot
            {
                PreorderRuleId = globalRule.Id,
                Label = "12 PM - 3 PM",
                StartTime = new TimeSpan(12, 0, 0),
                EndTime = new TimeSpan(15, 0, 0),
                SortOrder = 2,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PreorderSlot
            {
                PreorderRuleId = globalRule.Id,
                Label = "3 PM - 6 PM",
                StartTime = new TimeSpan(15, 0, 0),
                EndTime = new TimeSpan(18, 0, 0),
                SortOrder = 3,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            }
        );
        await db.SaveChangesAsync();
        Console.WriteLine("[PreorderSeeder] Seeded default inactive global rule with 3 time slots.");
    }
}
