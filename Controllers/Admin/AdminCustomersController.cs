using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin customers controller.
/// Routes: /api/v1/admin/customers
/// </summary>
[Route("api/v1/admin/customers")]
[Tags("Admin – Customers")]
public class AdminCustomersController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminCustomersController(DOSDbContext db, IConfiguration config) : base(config)
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
        if (!IsAdmin()) return AdminUnauthorized();
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
                Active = c.Status == 1
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
        if (!IsAdmin()) return AdminUnauthorized();
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
}
