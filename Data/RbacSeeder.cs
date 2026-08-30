using Microsoft.EntityFrameworkCore;
using DOSApi.Models.Rbac;

namespace DOSApi.Data;

/// <summary>
/// Seeds the Roles/Permissions RBAC schema and backfills pre-existing admins.
/// Table self-provisioning — this codebase doesn't run EF migrations at
/// startup, so any new table must be created defensively (same pattern as
/// DeliveryTypeSeeder/VendorCatalogSeeder).
/// </summary>
public static class RbacSeeder
{
    private const string SuperAdminSlug = "super-admin";
    private const string AdminSlug = "admin";
    private const string DataEvaluatorSlug = "data-evaluator";

    // Master feature catalog — one row per admin controller. Insert-missing
    // on every boot (not one-shot), so a new controller/feature added later
    // auto-registers its permission row without a manual DB step.
    private static readonly (string FeatureKey, string Label, string Section, int SortOrder)[] PermissionCatalog =
    {
        // Labels say "Products"/"Categories", not "Global Products"/"Global
        // Categories" — the feature_key keeps the "global_" prefix (it's
        // what AdminGlobalProductsController/AdminGlobalCategoriesController
        // actually check; renaming the key would need a data migration), but
        // the admin app's Products/Categories screens ARE these endpoints
        // (AdminProductController/AdminCategoryController, the non-global
        // ones, no longer exist), so a "Global Products" label next to a
        // "Products" screen in the app is exactly the mismatch that let a
        // Data Evaluator's write access look disabled when it wasn't.
        ("dashboard", "Dashboard", "Overview", 0),
        ("global_products", "Products", "Catalog", 20),
        ("global_categories", "Categories", "Catalog", 40),
        ("vendors", "Vendors", "Catalog", 60),
        ("banners", "Banners", "Catalog", 70),
        ("roadblocks", "Roadblock Popup", "Catalog", 71),
        ("customers", "Customers", "Sales", 80),
        ("orders", "Orders", "Sales", 90),
        // Split off "orders" so refund/cancel actions and the refund-window
        // policy can be granted independently of plain order viewing — see
        // the one-time migration below that preserves existing roles'
        // effective access across this split.
        ("refunds", "Refunds", "Sales", 91),
        ("refund_window", "Refund Window", "Sales", 92),
        ("transactions", "Transactions", "Sales", 100),
        ("roles_admins", "Roles & Admin Users", "Administration", 110),
        ("service_areas", "Service Areas", "Administration", 120),
        ("extra_charges", "Extra Charges", "Sales", 95),
        ("delivery_types", "Delivery Charges", "Sales", 96),
        ("payment_settings", "Payment Methods", "Sales", 97),
        ("preorder", "Preorder", "Sales", 98),
        ("min_order", "Minimum Order", "Sales", 99),
    };

    // Default grants applied ONLY the moment a role is first created — after
    // that, the Roles UI owns the matrix, so later boots never clobber an
    // admin's edits. Super Admin is the one exception (see below): its
    // grants are reasserted to full access on every boot so misconfiguring
    // it can never lock everyone out.
    private static readonly Dictionary<string, (bool CanRead, bool CanWrite)> AdminDefaults = new()
    {
        ["dashboard"] = (true, true),
        ["global_products"] = (true, true),
        ["global_categories"] = (true, true),
        ["vendors"] = (true, true),
        ["banners"] = (true, true),
        ["roadblocks"] = (true, true),
        ["customers"] = (true, true),
        ["orders"] = (true, true),
        ["refunds"] = (true, true),
        ["refund_window"] = (true, true),
        ["transactions"] = (true, true),
        ["service_areas"] = (true, true),
        ["extra_charges"] = (true, true),
        ["delivery_types"] = (true, true),
        ["payment_settings"] = (true, true),
        ["preorder"] = (true, true),
        ["min_order"] = (true, true),
        // roles_admins intentionally omitted — Super Admin only.
    };

    private static readonly Dictionary<string, (bool CanRead, bool CanWrite)> DataEvaluatorDefaults = new()
    {
        ["dashboard"] = (true, false),
        ["global_products"] = (true, true),
        ["global_categories"] = (true, true),
        ["vendors"] = (true, true),
        ["banners"] = (true, false),
        ["roadblocks"] = (true, false),
        ["customers"] = (true, false),
        ["orders"] = (true, false),
        ["refunds"] = (true, false),
        ["transactions"] = (true, false),
        // roles_admins, refund_window, extra_charges, delivery_types,
        // payment_settings, preorder, min_order intentionally omitted — config-level controls stay out of this
        // read-only analyst role's reach.
    };

    public static async Task EnsureTableAndSeedAsync(DOSDbContext db)
    {
        // Named admin_roles/admin_permissions/admin_role_permissions, NOT
        // roles/permissions — Bagisto's own admin panel already owns a
        // `roles` table (id/name/description/permission_type/permissions-
        // JSON) for its native PHP admin auth. `CREATE TABLE IF NOT EXISTS
        // roles` would silently no-op against that incompatible schema and
        // every query below would fail at runtime, so this schema uses its
        // own prefixed names instead.
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS admin_roles (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                name        VARCHAR(64)  NOT NULL UNIQUE,
                slug        VARCHAR(64)  NOT NULL UNIQUE,
                description VARCHAR(255) NULL,
                is_system   TINYINT(1)   NOT NULL DEFAULT 0,
                is_active   TINYINT(1)   NOT NULL DEFAULT 1,
                created_at  DATETIME     NULL,
                updated_at  DATETIME     NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ");

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS admin_permissions (
                id          INT AUTO_INCREMENT PRIMARY KEY,
                feature_key VARCHAR(64)  NOT NULL UNIQUE,
                label       VARCHAR(128) NOT NULL,
                section     VARCHAR(64)  NOT NULL,
                sort_order  INT          NOT NULL DEFAULT 0,
                created_at  DATETIME     NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ");

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS admin_role_permissions (
                id            INT AUTO_INCREMENT PRIMARY KEY,
                role_id       INT NOT NULL,
                permission_id INT NOT NULL,
                can_read      TINYINT(1) NOT NULL DEFAULT 0,
                can_write     TINYINT(1) NOT NULL DEFAULT 0,
                updated_at    DATETIME NULL,
                UNIQUE KEY UX_role_permission (role_id, permission_id),
                FOREIGN KEY (role_id) REFERENCES admin_roles(id) ON DELETE CASCADE,
                FOREIGN KEY (permission_id) REFERENCES admin_permissions(id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE=utf8_unicode_ci;
        ");

        // customer_admins predates RBAC — add the role FK column defensively.
        // MySQL < 8.0.29 doesn't support ADD COLUMN IF NOT EXISTS, so attempt
        // the ALTER and swallow "duplicate column" — same pattern as
        // VendorCatalogSeeder/DeviceTokenSeeder.
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE customer_admins ADD COLUMN role_id INT NULL");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060)
        {
            // Duplicate column — already present from a previous boot.
        }

        var now = DateTime.UtcNow;

        // ── Roles ────────────────────────────────────────────────────────
        var existingRoles = await db.Roles.ToListAsync();
        var newRoleSlugs = new List<string>();

        async Task<Role> EnsureRoleAsync(string name, string slug, string description, bool isSystem)
        {
            var existing = existingRoles.FirstOrDefault(r => r.Slug == slug);
            if (existing != null) return existing;

            var role = new Role
            {
                Name = name,
                Slug = slug,
                Description = description,
                IsSystem = isSystem,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            existingRoles.Add(role);
            newRoleSlugs.Add(slug);
            Console.WriteLine($"[RbacSeeder] Created role '{name}'.");
            return role;
        }

        var superAdminRole = await EnsureRoleAsync("Super Admin", SuperAdminSlug,
            "Full access to every feature, including Roles & Admin Users.", isSystem: true);
        var adminRole = await EnsureRoleAsync("Admin", AdminSlug,
            "Full access to business features. Cannot manage roles or admin users.", isSystem: false);
        var dataEvaluatorRole = await EnsureRoleAsync("Data Evaluator", DataEvaluatorSlug,
            "Can create/update Products, Categories and Vendors; read-only elsewhere.", isSystem: false);

        // ── Permission catalog ──────────────────────────────────────────
        var existingPermissions = await db.Permissions.ToListAsync();
        var newPermissionKeys = new List<string>();
        foreach (var (featureKey, label, section, sortOrder) in PermissionCatalog)
        {
            var existing = existingPermissions.FirstOrDefault(p => p.FeatureKey == featureKey);
            if (existing != null)
            {
                // Row already exists — still sync the label/section, so a
                // wording fix in code (e.g. dropping a "Global " prefix)
                // actually reaches the UI instead of being silently stuck
                // at whatever was seeded the first time this booted.
                if (existing.Label != label || existing.Section != section)
                {
                    existing.Label = label;
                    existing.Section = section;
                }
                continue;
            }
            var perm = new Permission
            {
                FeatureKey = featureKey,
                Label = label,
                Section = section,
                SortOrder = sortOrder,
                CreatedAt = now
            };
            db.Permissions.Add(perm);
            existingPermissions.Add(perm);
            newPermissionKeys.Add(featureKey);
        }
        await db.SaveChangesAsync();

        // ── Role grants ──────────────────────────────────────────────────
        var existingGrants = await db.RolePermissions.ToListAsync();

        // Super Admin: reasserted to full access on every boot, regardless
        // of prior state — this role must never be able to lock itself out.
        foreach (var perm in existingPermissions)
        {
            var grant = existingGrants.FirstOrDefault(g => g.RoleId == superAdminRole.Id && g.PermissionId == perm.Id);
            if (grant == null)
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = superAdminRole.Id,
                    PermissionId = perm.Id,
                    CanRead = true,
                    CanWrite = true,
                    UpdatedAt = now
                });
            }
            else if (!grant.CanRead || !grant.CanWrite)
            {
                grant.CanRead = true;
                grant.CanWrite = true;
                grant.UpdatedAt = now;
            }
        }

        // One-time migration: "refunds"/"refund_window" split off from
        // "orders" (2026-08). Every refund/cancel action and the refund-
        // window setting used to ride entirely on "orders" — for any role
        // that already existed before this split, copy its current "orders"
        // grant onto the new keys so existing admins don't silently lose
        // refund/cancel/refund-window access the moment this boots (same
        // "zero regression on deploy" principle as the Super Admin backfill
        // below). Only runs the boot each new key is introduced — after
        // that the Roles UI owns adjusting them independently, e.g. to
        // restrict "Refund Window" to Super Admin only while keeping
        // "Refunds" available to regular Admins.
        var ordersPerm = existingPermissions.FirstOrDefault(p => p.FeatureKey == "orders");
        if (ordersPerm != null)
        {
            foreach (var featureKey in new[] { "refunds", "refund_window" })
            {
                if (!newPermissionKeys.Contains(featureKey)) continue;
                var newPerm = existingPermissions.First(p => p.FeatureKey == featureKey);

                foreach (var role in existingRoles)
                {
                    if (role.Slug == SuperAdminSlug) continue; // reasserted to full access above regardless
                    if (newRoleSlugs.Contains(role.Slug)) continue; // gets proper defaults via SeedDefaultsIfNewRole

                    var ordersGrant = existingGrants.FirstOrDefault(g => g.RoleId == role.Id && g.PermissionId == ordersPerm.Id);
                    if (ordersGrant == null) continue; // role never had orders access — nothing to preserve

                    db.RolePermissions.Add(new RolePermission
                    {
                        RoleId = role.Id,
                        PermissionId = newPerm.Id,
                        CanRead = ordersGrant.CanRead,
                        CanWrite = ordersGrant.CanWrite,
                        UpdatedAt = now
                    });
                    Console.WriteLine($"[RbacSeeder] Migrated '{role.Name}' orders access to new '{featureKey}' permission.");
                }
            }
        }

        // Admin / Data Evaluator: seed defaults ONLY the boot each role is
        // first created — after that the Roles UI owns the matrix.
        void SeedDefaultsIfNewRole(Role role, Dictionary<string, (bool CanRead, bool CanWrite)> defaults)
        {
            if (!newRoleSlugs.Contains(role.Slug)) return;
            foreach (var perm in existingPermissions)
            {
                if (!defaults.TryGetValue(perm.FeatureKey, out var grant)) continue;
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = perm.Id,
                    CanRead = grant.CanRead,
                    CanWrite = grant.CanWrite,
                    UpdatedAt = now
                });
            }
        }
        SeedDefaultsIfNewRole(adminRole, AdminDefaults);
        SeedDefaultsIfNewRole(dataEvaluatorRole, DataEvaluatorDefaults);

        await db.SaveChangesAsync();

        // ── Backfill pre-existing admins ────────────────────────────────
        // Every customer_admins row that predates RBAC becomes Super Admin,
        // preserving current access exactly (zero regression on deploy).
        var backfilled = await db.Database.ExecuteSqlRawAsync(
            "UPDATE customer_admins SET role_id = {0} WHERE role_id IS NULL",
            superAdminRole.Id);
        if (backfilled > 0)
        {
            Console.WriteLine($"[RbacSeeder] Backfilled {backfilled} existing admin(s) to Super Admin.");
        }

        Console.WriteLine($"[RbacSeeder] {existingRoles.Count} role(s), {existingPermissions.Count} permission(s) ready.");
    }
}
