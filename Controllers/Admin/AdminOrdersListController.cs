using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Sales;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin orders controller.
/// Routes: /api/v1/admin/global-orders
/// </summary>
[Route("api/v1/admin/global-orders")]
[Tags("Admin � Orders")]
public class AdminOrdersListController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminOrdersListController(DOSDbContext db, IConfiguration config) : base(db, config)
    {
        _db = db;
    }

    // An order can mix products from multiple vendors (marketplace-style);
    // the DTO only has room for one vendorName string, so this takes the
    // first item whose product resolves to a vendor. Best-effort, not exact
    // for genuinely mixed-vendor orders — no worse than a single-vendor
    // assumption baked elsewhere in this DTO shape.
    private static string ResolveOrderVendorName(Order order)
    {
        foreach (var item in order.Items)
        {
            var name = ProductService.ExtractVendorName(item.Product?.Additional, "en");
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return "";
    }

    /// <summary>List all orders with search and filter</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null)
    {
        if (!await HasPermissionAsync("orders")) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .AsNoTracking();

        // Search filter (order ID, customer name, vendor name)
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(o =>
                (o.IncrementId != null && o.IncrementId.ToLower().Contains(searchLower)) ||
                (o.CustomerFirstName != null && o.CustomerFirstName.ToLower().Contains(searchLower)) ||
                (o.CustomerLastName != null && o.CustomerLastName.ToLower().Contains(searchLower)) ||
                (o.CustomerEmail != null && o.CustomerEmail.ToLower().Contains(searchLower)));
        }

        // Status filter (no cancelled tab in global orders)
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(o => o.Status == status.ToLower());
        }
        else
        {
            // Exclude cancelled by default when no status filter
            query = query.Where(o => o.Status != "cancelled" && o.Status != "canceled");
        }

        var total = await query.CountAsync();
        var pagedOrders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        // Vendor resolution needs the Items/Product navigation already
        // loaded above (JSON parsing can't be pushed into SQL), so this
        // mapping happens in-memory rather than as part of the query.
        var orders = pagedOrders.Select(o => new OrderListDto
        {
            Id = o.Id,
            IncrementId = o.IncrementId ?? "",
            PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
            Status = o.Status ?? "pending",
            GrandTotal = o.GrandTotal ?? 0,
            ItemsCount = o.TotalItemCount ?? 0,
            CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
            CustomerPhone = o.CustomerEmail ?? "",
            VendorName = ResolveOrderVendorName(o),
            PaymentMethod = "", // TODO: Get from payment
            DeliveryAddress = "" // TODO: Get from address
        }).ToList();

        return Ok(new OrderListResponse
        {
            Data = orders,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            }
        });
    }

    /// <summary>Get order detail</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("orders")) return AdminUnauthorized();

        var order = await _db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
            return NotFound(new { message = "Order not found" });

        var response = new OrderDetailResponse
        {
            Data = new OrderDetailDto
            {
                Id = order.Id,
                IncrementId = order.IncrementId ?? "",
                PlacedAt = order.CreatedAt ?? DateTime.UtcNow,
                Status = order.Status ?? "pending",
                GrandTotal = order.GrandTotal ?? 0,
                ItemsCount = order.TotalItemCount ?? 0,
                VendorName = ResolveOrderVendorName(order),
                CustomerName = order.CustomerFirstName + " " + order.CustomerLastName,
                CustomerPhone = order.CustomerEmail ?? "",
                PaymentMethod = "", // TODO: Get from payment
                DeliveryAddress = "", // TODO: Get from address
                Items = order.Items.Select(item => new OrderItemDto
                {
                    Name = item.Name ?? "",
                    Qty = (int)(item.QtyOrdered ?? 0),
                    Price = item.Price ?? 0
                }).ToList()
            }
        };

        return Ok(response);
    }

    /// <summary>Update order status</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateOrderStatusRequest request)
    {
        if (!await HasPermissionAsync("orders", requireWrite: true)) return AdminForbidden("orders");
        if (string.IsNullOrWhiteSpace(request.Status))
            return BadRequest(new { message = "status field is required" });

        var order = await _db.Orders.FindAsync(id);
        if (order == null)
            return NotFound(new { message = "Order not found" });

        // Validate status transitions
        var validTransitions = new Dictionary<string, string[]>
        {
            { "pending", new[] { "processing" } },
            { "processing", new[] { "shipped" } },
            { "shipped", new[] { "completed" } },
            { "completed", System.Array.Empty<string>() }
        };

        var currentStatus = order.Status?.ToLower() ?? "pending";
        var newStatus = request.Status.ToLower();

        if (!validTransitions.ContainsKey(currentStatus))
            return BadRequest(new { message = $"Cannot update status from '{currentStatus}'" });

        if (!validTransitions[currentStatus].Contains(newStatus))
            return BadRequest(new { message = $"Cannot update status from '{currentStatus}'" });

        order.Status = newStatus;
        await _db.SaveChangesAsync();

        var statusDisplay = newStatus switch
        {
            "processing" => "Processing",
            "shipped" => "Shipped",
            "completed" => "Completed",
            _ => newStatus
        };

        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = order.Id, status = newStatus },
            Message = $"Status updated to {statusDisplay}"
        });
    }
}
