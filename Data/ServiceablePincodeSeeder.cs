using Microsoft.EntityFrameworkCore;
using DOSApi.Models;

namespace DOSApi.Data;

public static class ServiceablePincodeSeeder
{
    public static async Task EnsureTableAndSeedAsync(DOSDbContext db)
    {
        // Table self-provisioning — this codebase doesn't run EF migrations at
        // startup, so any new table must be created defensively.
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS serviceable_pincodes (
                id              INT AUTO_INCREMENT PRIMARY KEY,
                pincode         VARCHAR(16)  NOT NULL UNIQUE,
                town            VARCHAR(128) NOT NULL,
                district        VARCHAR(128) NULL,
                postal_division VARCHAR(128) NULL,
                is_active       TINYINT(1)   NOT NULL DEFAULT 1,
                created_at      DATETIME     NULL,
                updated_at      DATETIME     NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ";
        await db.Database.ExecuteSqlRawAsync(createTableSql);

        if (await db.ServiceablePincodes.AnyAsync())
        {
            Console.WriteLine("[ServiceablePincodeSeeder] serviceable_pincodes already seeded. Skipping.");
            return;
        }

        var now = DateTime.UtcNow;
        db.ServiceablePincodes.AddRange(
            new ServiceablePincode
            {
                Pincode = "523247",
                Town = "Darsi",
                District = "Prakasam",
                PostalDivision = "Markapur Division",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ServiceablePincode
            {
                Pincode = "523240",
                Town = "Podili",
                District = "Prakasam",
                PostalDivision = "Markapur Division",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ServiceablePincode
            {
                Pincode = "523230",
                Town = "Kanigiri",
                District = "Prakasam",
                PostalDivision = "Markapur Division",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            }
        );

        await db.SaveChangesAsync();
        Console.WriteLine("[ServiceablePincodeSeeder] Seeded 3 serviceable pincodes.");
    }
}
