using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin orders controller.
/// Routes: /api/v1/admin/global-orders
/// </summary>
[Route("api/v1/admin/global-orders")]
[Tags("Admin – Orders")]
public class AdminOrdersListController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminOrdersListController(DOSDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    /// <summary>List all orders with search and filter</summary>
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

        var query = _db.Orders.AsNoTracking();

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
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(o => new OrderListDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                ItemsCount = o.TotalItemCount ?? 0,
                CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
                CustomerPhone = o.CustomerEmail ?? "",
                VendorName = "N/A", // TODO: Get from vendor relation if available
                PaymentMethod = "", // TODO: Get from payment
                DeliveryAddress = "" // TODO: Get from address
            })
            .ToListAsync();

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
        if (!IsAdmin()) return AdminUnauthorized();

        var order = await _db.Orders
            .Include(o => o.Items)
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
                VendorName = "N/A", // TODO: Get from vendor relation
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
        if (!IsAdmin()) return AdminUnauthorized();
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
