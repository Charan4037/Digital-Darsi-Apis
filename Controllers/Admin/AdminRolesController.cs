using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Rbac;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin roles &amp; permissions controller.
/// Routes: /api/v1/admin/roles
/// </summary>
[Route("api/v1/admin/roles")]
[Tags("Admin – Roles")]
public class AdminRolesController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminRolesController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    /// <summary>List all roles</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!await HasPermissionAsync("roles_admins")) return AdminUnauthorized();

        var adminCounts = await _db.CustomerAdmins
            .Where(a => a.RoleId != null)
            .GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToListAsync();

        var roles = await _db.Roles
            .AsNoTracking()
            .OrderBy(r => r.IsSystem ? 0 : 1).ThenBy(r => r.Name)
            .ToListAsync();

        var result = roles.Select(r => new RoleDto
        {
            Id = r.Id,
            Name = r.Name,
            Slug = r.Slug,
            Description = r.Description,
            IsSystem = r.IsSystem,
            IsActive = r.IsActive,
            AdminUserCount = adminCounts.FirstOrDefault(c => c.RoleId == r.Id)?.Count ?? 0
        }).ToList();

        return Ok(new RoleListResponse { Data = result });
    }

    /// <summary>The caller's own resolved permission map</summary>
    /// <remarks>
    /// Fetched once when the Flutter admin module opens, to decide which
    /// Quick Actions tiles to show. UI convenience only — the server remains
    /// the sole enforcement authority on every actual request.
    /// </remarks>
    [HttpGet("/api/v1/admin/me/permissions")]
    public async Task<IActionResult> MyPermissions()
    {
        if (!await IsAdminAsync()) return AdminUnauthorized();

        var customerId = CurrentCustomerId();
        var map = new Dictionary<string, PermissionFlags>();

        if (customerId != null)
        {
            var grants = await _db.CustomerAdmins
                .Where(a => a.CustomerId == customerId && a.RoleId != null)
                .Join(_db.RolePermissions, a => a.RoleId, rp => rp.RoleId, (a, rp) => rp)
                .Join(_db.Permissions, rp => rp.PermissionId, p => p.Id,
                    (rp, p) => new { p.FeatureKey, rp.CanRead, rp.CanWrite })
                .ToListAsync();

            foreach (var g in grants)
            {
                map[g.FeatureKey] = new PermissionFlags { CanRead = g.CanRead, CanWrite = g.CanWrite };
            }
        }

        return Ok(new MyPermissionsResponse { Data = map });
    }

    /// <summary>Master permission catalog (feature list for the permission matrix UI)</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> ListPermissions()
    {
        if (!await HasPermissionAsync("roles_admins")) return AdminUnauthorized();

        var permissions = await _db.Permissions
            .AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .Select(p => new PermissionDto { FeatureKey = p.FeatureKey, Label = p.Label, Section = p.Section })
            .ToListAsync();

        return Ok(new PermissionListResponse { Data = permissions });
    }

    /// <summary>Get a single role with its full permission matrix</summary>
    /// <remarks>
    /// Every permission in the catalog is included, even if this role has no
    /// grant for it (shown as read=false, write=false) — so the Flutter form
    /// always renders the complete matrix rather than only granted rows.
    /// </remarks>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("roles_admins")) return AdminUnauthorized();

        var role = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound(new { success = false, message = "Role not found." });

        var permissions = await _db.Permissions.AsNoTracking().OrderBy(p => p.SortOrder).ToListAsync();
        var grants = await _db.RolePermissions.AsNoTracking().Where(g => g.RoleId == id).ToListAsync();

        var matrix = permissions.Select(p =>
        {
            var grant = grants.FirstOrDefault(g => g.PermissionId == p.Id);
            return new PermissionGrantDto
            {
                FeatureKey = p.FeatureKey,
                Label = p.Label,
                Section = p.Section,
                CanRead = grant?.CanRead ?? false,
                CanWrite = grant?.CanWrite ?? false
            };
        }).ToList();

        return Ok(new RoleDetailResponse
        {
            Data = new RoleDetailDto
            {
                Id = role.Id,
                Name = role.Name,
                Slug = role.Slug,
                Description = role.Description,
                IsSystem = role.IsSystem,
                IsActive = role.IsActive,
                Permissions = matrix
            }
        });
    }

    /// <summary>Create a new role with a permission matrix</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request)
    {
        if (!await HasPermissionAsync("roles_admins", requireWrite: true)) return AdminForbidden("roles_admins");
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { success = false, message = "Name is required." });

        var slug = Slugify(request.Name);
        if (await _db.Roles.AnyAsync(r => r.Slug == slug))
            return Conflict(new { success = false, message = "A role with this name already exists." });

        var now = DateTime.UtcNow;
        var role = new Role
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Description = request.Description,
            IsSystem = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        await ReplaceGrantsAsync(role.Id, request.Permissions, now);

        return await Get(role.Id);
    }

    /// <summary>Update a role's name/description and permission matrix</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateRoleRequest request)
    {
        if (!await HasPermissionAsync("roles_admins", requireWrite: true)) return AdminForbidden("roles_admins");

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound(new { success = false, message = "Role not found." });
        if (role.IsSystem)
            return Conflict(new { success = false, message = "The Super Admin role is protected and cannot be edited." });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { success = false, message = "Name is required." });

        var slug = Slugify(request.Name);
        if (await _db.Roles.AnyAsync(r => r.Id != id && r.Slug == slug))
            return Conflict(new { success = false, message = "A role with this name already exists." });

        var now = DateTime.UtcNow;
        role.Name = request.Name.Trim();
        role.Slug = slug;
        role.Description = request.Description;
        role.UpdatedAt = now;
        await _db.SaveChangesAsync();

        await ReplaceGrantsAsync(role.Id, request.Permissions, now);

        return await Get(role.Id);
    }

    /// <summary>Delete a role</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync("roles_admins", requireWrite: true)) return AdminForbidden("roles_admins");

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound(new { success = false, message = "Role not found." });
        if (role.IsSystem)
            return Conflict(new { success = false, message = "The Super Admin role is protected and cannot be deleted." });

        var inUse = await _db.CustomerAdmins.AnyAsync(a => a.RoleId == id);
        if (inUse)
            return Conflict(new { success = false, message = "This role is still assigned to one or more admin users." });

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();

        return Ok(new MessageResponse { Message = $"'{role.Name}' deleted." });
    }

    // Replaces this role's full grant set. Write implies read is enforced
    // here too (defense in depth alongside the Flutter form's own toggle
    // logic) so a malformed payload can't produce a write-only grant.
    private async Task ReplaceGrantsAsync(int roleId, List<PermissionGrantRequest> requested, DateTime now)
    {
        var existing = await _db.RolePermissions.Where(g => g.RoleId == roleId).ToListAsync();
        _db.RolePermissions.RemoveRange(existing);

        if (requested.Count > 0)
        {
            var permissions = await _db.Permissions
                .Where(p => requested.Select(r => r.FeatureKey).Contains(p.FeatureKey))
                .ToListAsync();

            foreach (var req in requested)
            {
                var perm = permissions.FirstOrDefault(p => p.FeatureKey == req.FeatureKey);
                if (perm == null) continue;

                var canWrite = req.CanWrite;
                var canRead = req.CanRead || canWrite;

                _db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    PermissionId = perm.Id,
                    CanRead = canRead,
                    CanWrite = canWrite,
                    UpdatedAt = now
                });
            }
        }

        await _db.SaveChangesAsync();
    }
}
