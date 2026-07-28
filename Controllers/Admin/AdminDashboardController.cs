using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin dashboard controller.
/// Routes: /api/v1/admin/dashboard
/// </summary>
[Route("api/v1/admin/dashboard")]
[Tags("Admin � Dashboard")]
public class AdminDashboardController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly VendorAggregationService _aggregation;

    public AdminDashboardController(DOSDbContext db, IConfiguration config, VendorAggregationService aggregation) : base(db, config)
    {
        _db = db;
        _aggregation = aggregation;
    }

    /// <summary>Get dashboard overview with stats and recent data</summary>
    /// <remarks>
    /// Returns comprehensive dashboard data including:
    /// - Stats: Total vendors, products, orders, customers, revenue
    /// - Recent vendors: Top 3 most active vendors
    /// - Recent orders: Top 4 most recent orders
    ///
    /// Visible to any admin role regardless of granular grants — this is a
    /// summary landing page, not a distinct manageable resource.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetDashboard()
    {
        if (!await IsAdminAsync()) return AdminUnauthorized();

        var data = new DashboardData();

        // Calculate stats
        var currentMonth = DateTime.UtcNow.Month;
        var currentYear = DateTime.UtcNow.Year;

        data.Stats.TotalVendors = await _db.Vendors.CountAsync();
        data.Stats.ActiveVendors = await _db.Vendors.Where(v => v.Active).CountAsync();
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
        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorAggregates = await _aggregation.BuildVendorAggregatesAsync(productVendorMap);

        var allVendors = await _db.Vendors.AsNoTracking().ToListAsync();
        data.RecentVendors = allVendors
            .Select(v =>
            {
                vendorAggregates.TryGetValue(v.Name, out var agg);
                return new RecentVendorDto
                {
                    Id = v.Id,
                    Name = v.Name,
                    Products = agg?.Products ?? 0,
                    Orders = agg?.Orders ?? 0,
                    Revenue = agg?.Revenue ?? 0,
                    Active = v.Active
                };
            })
            .OrderByDescending(v => v.Revenue)
            .Take(3)
            .ToList();

        // Recent orders (top 4)
        var recentOrdersRaw = await _db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .OrderByDescending(o => o.CreatedAt)
            .Take(4)
            .AsNoTracking()
            .ToListAsync();

        data.RecentOrders = recentOrdersRaw.Select(o => new RecentOrderDto
        {
            Id = o.Id,
            IncrementId = o.IncrementId ?? "",
            Status = o.Status ?? "pending",
            GrandTotal = o.GrandTotal ?? 0,
            CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
            VendorName = o.Items
                .Select(i => ProductService.ExtractVendorName(i.Product?.Additional, "en"))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? ""
        }).ToList();

        return Ok(new DashboardResponse { Data = data });
    }
}
