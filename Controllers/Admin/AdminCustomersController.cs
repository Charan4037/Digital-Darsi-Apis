using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Rbac;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin customers controller.
/// Routes: /api/v1/admin/customers
/// </summary>
[Route("api/v1/admin/customers")]
[Tags("Admin � Customers")]
public class AdminCustomersController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminCustomersController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    /// <summary>List all customers with search and filter</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null)
    {
        if (!await HasPermissionAsync("customers")) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Customers
            .Include(c => c.Orders)
            .AsNoTracking();

        // Search filter (name, email, city)
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(c =>
                (c.FirstName + " " + c.LastName).ToLower().Contains(searchLower) ||
                (c.Email != null && c.Email.ToLower().Contains(searchLower)));
        }

        // Status filter
        if (!string.IsNullOrWhiteSpace(status))
        {
            bool isActive = status.Equals("active", StringComparison.OrdinalIgnoreCase);
            query = query.Where(c => (c.Status == 1) == isActive);
        }

        var total = await query.CountAsync();
        var customers = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(c => new CustomerDto
            {
                Id = c.Id,
                Name = c.FirstName + " " + c.LastName,
                Email = c.Email ?? "",
                Phone = c.Phone ?? "",
                City = "", // TODO: Add city field if needed
                Orders = c.Orders.Count,
                TotalSpent = c.Orders.Where(o => o.Status != "canceled").Sum(o => o.GrandTotal ?? 0),
                JoinedAt = c.CreatedAt ?? DateTime.UtcNow,
                Active = c.Status == 1,
                AppVersion = _db.CustomerDeviceTokens
                    .Where(t => t.CustomerId == c.Id)
                    .OrderByDescending(t => t.LastSeenAt ?? t.UpdatedAt)
                    .Select(t => t.AppVersion)
                    .FirstOrDefault(),
                Platform = _db.CustomerDeviceTokens
                    .Where(t => t.CustomerId == c.Id)
                    .OrderByDescending(t => t.LastSeenAt ?? t.UpdatedAt)
                    .Select(t => t.Platform)
                    .FirstOrDefault(),
            })
            .ToListAsync();

        return Ok(new CustomerListResponse
        {
            Data = customers,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            }
        });
    }

    /// <summary>Update customer status (suspend/enable)</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!await HasPermissionAsync("customers", requireWrite: true)) return AdminForbidden("customers");
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

        var customer = await _db.Customers.FindAsync(id);
        if (customer == null)
            return NotFound(new { message = "Customer not found" });

        customer.Status = request.Active.Value ? 1 : 0;
        await _db.SaveChangesAsync();

        var action = request.Active.Value ? "enabled" : "suspended";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = customer.Id, active = request.Active.Value },
            Message = $"{customer.FirstName} {customer.LastName} {action}"
        });
    }

    // ─── Admin users (staff) ────────────────────────────────────────────────
    // "Admin users" are just customers with a customer_admins row + a role —
    // no separate resource, so these live alongside the customer endpoints.

    /// <summary>List current staff (customers with an admin role)</summary>
    [HttpGet("admins")]
    public async Task<IActionResult> ListAdmins()
    {
        if (!await HasPermissionAsync("roles_admins")) return AdminUnauthorized();

        var admins = await _db.CustomerAdmins
            .Where(a => a.RoleId != null)
            .Join(_db.Customers, a => a.CustomerId, c => c.Id, (a, c) => new { a, c })
            .Join(_db.Roles, x => x.a.RoleId, r => r.Id, (x, r) => new AdminUserDto
            {
                CustomerId = x.c.Id,
                Name = (x.c.FirstName + " " + x.c.LastName).Trim(),
                Email = x.c.Email ?? "",
                Phone = x.c.Phone ?? "",
                RoleId = r.Id,
                RoleName = r.Name,
                RoleSlug = r.Slug
            })
            .OrderBy(d => d.Name)
            .ToListAsync();

        return Ok(new AdminUserListResponse { Data = admins });
    }

    /// <summary>Promote a customer to staff with the given role</summary>
    [HttpPost("{id:int}/promote")]
    public async Task<IActionResult> Promote(int id, [FromBody] AssignRoleRequest request)
    {
        if (!await HasPermissionAsync("roles_admins", requireWrite: true)) return AdminForbidden("roles_admins");

        var customer = await _db.Customers.FindAsync(id);
        if (customer == null) return NotFound(new { success = false, message = "Customer not found." });

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId && r.IsActive);
        if (role == null) return NotFound(new { success = false, message = "Role not found." });

        var existing = await _db.CustomerAdmins.FirstOrDefaultAsync(a => a.CustomerId == id);
        if (existing != null)
        {
            existing.RoleId = role.Id;
        }
        else
        {
            _db.CustomerAdmins.Add(new DOSApi.Models.Customer.CustomerAdmin
            {
                CustomerId = id,
                RoleId = role.Id,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();
        ClearPermissionCache();

        return Ok(new MessageResponse { Message = $"{customer.FirstName} {customer.LastName} is now {role.Name}." });
    }

    /// <summary>Change an existing admin user's role</summary>
    [HttpPut("{id:int}/role")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] AssignRoleRequest request)
    {
        if (!await HasPermissionAsync("roles_admins", requireWrite: true)) return AdminForbidden("roles_admins");

        var admin = await _db.CustomerAdmins.FirstOrDefaultAsync(a => a.CustomerId == id);
        if (admin == null) return NotFound(new { success = false, message = "This customer is not an admin user." });

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId && r.IsActive);
        if (role == null) return NotFound(new { success = false, message = "Role not found." });

        if (await WouldRemoveLastSuperAdminAsync(admin, role.Slug))
            return Conflict(new { success = false, message = "At least one Super Admin must remain." });

        admin.RoleId = role.Id;
        await _db.SaveChangesAsync();
        ClearPermissionCache();

        return Ok(new MessageResponse { Message = $"Role updated to {role.Name}." });
    }

    /// <summary>Revoke a customer's admin/staff access entirely</summary>
    [HttpDelete("{id:int}/admin")]
    public async Task<IActionResult> Demote(int id)
    {
        if (!await HasPermissionAsync("roles_admins", requireWrite: true)) return AdminForbidden("roles_admins");

        var admin = await _db.CustomerAdmins.FirstOrDefaultAsync(a => a.CustomerId == id);
        if (admin == null) return NotFound(new { success = false, message = "This customer is not an admin user." });

        if (await WouldRemoveLastSuperAdminAsync(admin, newRoleSlug: null))
            return Conflict(new { success = false, message = "At least one Super Admin must remain." });

        _db.CustomerAdmins.Remove(admin);
        await _db.SaveChangesAsync();
        ClearPermissionCache();

        return Ok(new MessageResponse { Message = "Admin access revoked." });
    }

    // True if changing/removing `admin`'s role would leave zero active Super
    // Admins. `newRoleSlug` is the role they're moving TO (null when demoting
    // entirely) — only relevant when `admin` is currently a Super Admin.
    private async Task<bool> WouldRemoveLastSuperAdminAsync(DOSApi.Models.Customer.CustomerAdmin admin, string? newRoleSlug)
    {
        if (newRoleSlug == "super-admin") return false;

        var isCurrentlySuperAdmin = admin.RoleId != null &&
            await _db.Roles.AnyAsync(r => r.Id == admin.RoleId && r.Slug == "super-admin");
        if (!isCurrentlySuperAdmin) return false;

        var otherSuperAdmins = await _db.CustomerAdmins
            .Where(a => a.Id != admin.Id)
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => r.Slug)
            .CountAsync(slug => slug == "super-admin");

        return otherSuperAdmins == 0;
    }
}
