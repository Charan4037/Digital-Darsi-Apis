using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin dashboard controller.
/// Routes: /api/v1/admin/dashboard
/// </summary>
[Route("api/v1/admin/dashboard")]
[Tags("Admin – Dashboard")]
public class AdminDashboardController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminDashboardController(DOSDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    /// <summary>Get dashboard overview with stats and recent data</summary>
    /// <remarks>
    /// Returns comprehensive dashboard data including:
    /// - Stats: Total vendors, products, orders, customers, revenue
    /// - Recent vendors: Top 3 most active vendors
    /// - Recent orders: Top 4 most recent orders
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetDashboard()
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var data = new DashboardData();

        // Calculate stats
        var currentMonth = DateTime.UtcNow.Month;
        var currentYear = DateTime.UtcNow.Year;

        data.Stats.TotalVendors = await _db.Customers.CountAsync();
        data.Stats.ActiveVendors = await _db.Customers.Where(c => c.Status == 1).CountAsync();
        data.Stats.TotalProducts = await _db.Products.Where(p => p.ParentId == null).CountAsync();
        data.Stats.TotalOrders = await _db.Orders.CountAsync();
        data.Stats.TotalCustomers = await _db.Customers.CountAsync();

        data.Stats.TotalRevenue = await _db.Orders
            .Where(o => o.Status != "canceled")
            .SumAsync(o => o.GrandTotal ?? 0);

        data.Stats.MonthRevenue = await _db.Orders
            .Where(o => o.Status != "canceled" && o.CreatedAt.HasValue &&
                        o.CreatedAt.Value.Month == currentMonth &&
                        o.CreatedAt.Value.Year == currentYear)
            .SumAsync(o => o.GrandTotal ?? 0);

        data.Stats.PendingOrders = await _db.Orders
            .Where(o => o.Status == "pending")
            .CountAsync();

        // Recent vendors (top 3 by revenue)
        data.RecentVendors = await _db.Customers
            .Include(c => c.Orders)
            .OrderByDescending(c => c.Orders.Sum(o => o.GrandTotal ?? 0))
            .Take(3)
            .Select(c => new RecentVendorDto
            {
                Id = c.Id,
                Name = c.FirstName + " " + c.LastName,
                Products = 0, // TODO: Calculate from products table if vendors are stored there
                Orders = c.Orders.Count,
                Revenue = c.Orders.Where(o => o.Status != "canceled").Sum(o => o.GrandTotal ?? 0),
                Active = c.Status == 1
            })
            .ToListAsync();

        // Recent orders (top 4)
        data.RecentOrders = await _db.Orders
            .OrderByDescending(o => o.CreatedAt)
            .Take(4)
            .Select(o => new RecentOrderDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
                VendorName = "N/A" // TODO: Get from vendor relation if available
            })
            .ToListAsync();

        return Ok(new DashboardResponse { Data = data });
    }
}
