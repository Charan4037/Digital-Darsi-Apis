using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Sales;
using DOSApi.Models.Customer;
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
    private readonly AccountService _accountService;

    public AdminOrdersListController(DOSDbContext db, AccountService accountService, IConfiguration config) : base(db, config)
    {
        _db = db;
        _accountService = accountService;
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
            // Variant products (e.g. "SKU-1") carry only variant metadata in their own
            // `additional` JSON — vendor info lives on the parent/configurable product.
            var name = ProductService.ExtractVendorName(item.Product?.Additional, "en");
            if (string.IsNullOrWhiteSpace(name))
                name = ProductService.ExtractVendorName(item.Product?.Parent?.Additional, "en");
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return "";
    }

    // Order.CustomerFirstName/LastName are sometimes blank (a checkout-flow gap that
    // predates the fix in CheckoutService) — fall back to the address name, which is
    // always populated, for those orders.
    private static string ResolveCustomerName(Order order, Address? addr)
    {
        var name = $"{order.CustomerFirstName} {order.CustomerLastName}".Trim();
        if (string.IsNullOrWhiteSpace(name) && addr != null)
            name = $"{addr.FirstName} {addr.LastName}".Trim();
        return name;
    }

    private static Address? PickDeliveryAddress(List<Address> addresses) =>
        addresses.FirstOrDefault(a => a.AddressType == "order_shipping")
        ?? addresses.FirstOrDefault(a => a.AddressType == "order_billing")
        ?? addresses.FirstOrDefault();

    private static string FormatAddress(Address? a) => a == null
        ? ""
        : string.Join(", ", new[] { a.AddressLine, a.City, a.State, a.Postcode }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    // Same raw-code check as AccountService/OrderInvoiceService — cash-on-delivery
    // collects no money until the order is delivered, which the admin UI uses to
    // decide when the "Issue Refund" button makes sense to offer.
    private static bool IsCod(Order order) =>
        string.Equals(order.Payment?.Method, "cashondelivery", StringComparison.OrdinalIgnoreCase);

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
            .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent)
            .Include(o => o.Payment)
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

        // Addresses aren't a real EF navigation on Order (see BagistoDbContext), so load
        // them separately and group by order id, same pattern as AdminOrderController.
        var orderIds = pagedOrders.Select(o => o.Id).ToList();
        var addressesByOrder = await _db.Addresses
            .Where(a => a.OrderId != null && orderIds.Contains(a.OrderId.Value))
            .AsNoTracking()
            .ToListAsync();

        // Vendor resolution needs the Items/Product navigation already
        // loaded above (JSON parsing can't be pushed into SQL), so this
        // mapping happens in-memory rather than as part of the query.
        var orders = pagedOrders.Select(o =>
        {
            var deliveryAddr = PickDeliveryAddress(addressesByOrder.Where(a => a.OrderId == o.Id).ToList());
            return new OrderListDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                ItemsCount = o.TotalItemCount ?? 0,
                CustomerName = ResolveCustomerName(o, deliveryAddr),
                CustomerPhone = deliveryAddr?.Phone ?? "",
                VendorName = ResolveOrderVendorName(o),
                PaymentMethod = o.Payment?.MethodTitle ?? o.Payment?.Method ?? "",
                IsCod = IsCod(o),
                DeliveryAddress = FormatAddress(deliveryAddr)
            };
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
            .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent)
            .Include(o => o.Payment)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
            return NotFound(new { message = "Order not found" });

        // Addresses aren't a real EF navigation on Order (see BagistoDbContext), so load
        // them separately, same pattern as AdminOrderController / OrderInvoiceService.
        var addresses = await _db.Addresses
            .Where(a => a.OrderId == id)
            .AsNoTracking()
            .ToListAsync();
        var deliveryAddr = PickDeliveryAddress(addresses);

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
                CustomerName = ResolveCustomerName(order, deliveryAddr),
                CustomerPhone = deliveryAddr?.Phone ?? "",
                PaymentMethod = order.Payment?.MethodTitle ?? order.Payment?.Method ?? "",
                IsCod = IsCod(order),
                DeliveredAt = AccountService.ResolveDeliveredAt(order),
                RefundWindowDays = await _accountService.GetRefundWindowDaysAsync(),
                DeliveryAddress = FormatAddress(deliveryAddr),
                Items = order.Items.Where(i => i.ParentId == null).Select(item => new OrderItemDto
                {
                    Id = item.Id,
                    Name = item.Name ?? "",
                    Qty = (int)(item.QtyOrdered ?? 0),
                    QtyCanceled = item.QtyCanceled ?? 0,
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
        if (newStatus == "completed") order.DeliveredAt ??= DateTime.UtcNow;
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

    /// <summary>Cancel an order (admin-initiated)</summary>
    /// <remarks>
    /// Lets an admin cancel an order that hasn't shipped yet — e.g. a stock, fraud, or
    /// fulfillment issue found after checkout. Only allowed while the order is still
    /// "pending" or "processing"; restores stock for its items. Does NOT create a refund —
    /// refunds are always a separate, explicit action. If the order was paid for online,
    /// follow up with `POST /api/v1/admin/orders/{id}/refund` to send the money back.
    /// </remarks>
    /// <param name="id">Order database ID</param>
    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!await HasPermissionAsync("orders", requireWrite: true)) return AdminForbidden("orders");

        var (success, message) = await _accountService.AdminCancelOrderAsync(id);
        if (!success) return BadRequest(new { message });

        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id, status = "canceled" },
            Message = message
        });
    }

    public record CancelOrderItemsRequest(List<CancelItemBody> Items);
    public record CancelItemBody(int OrderItemId, int Qty);

    /// <summary>Cancel specific item(s) within an order (admin-initiated)</summary>
    /// <remarks>
    /// Only allowed while the order is still "pending" or "processing" (not yet shipped).
    /// Send each item's full remaining quantity to cancel — restores stock for those
    /// quantities. Does not create a refund; follow up with
    /// `POST /api/v1/admin/orders/{id}/refund` if the order was paid for online.
    /// </remarks>
    /// <param name="id">Order database ID</param>
    [HttpPost("{id:int}/cancel-items")]
    public async Task<IActionResult> CancelItems(int id, [FromBody] CancelOrderItemsRequest req)
    {
        if (!await HasPermissionAsync("orders", requireWrite: true)) return AdminForbidden("orders");

        if (req.Items == null || req.Items.Count == 0)
            return BadRequest(new { message = "Select at least one item to cancel." });

        var items = req.Items.Select(i => new AccountService.RefundItemRequest(i.OrderItemId, i.Qty)).ToList();
        var (success, message, orderFullyCanceled) = await _accountService.AdminCancelOrderItemsAsync(id, items);
        if (!success) return BadRequest(new { message });

        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id, orderFullyCanceled },
            Message = message
        });
    }
}
