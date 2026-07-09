using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Sales;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin read + status management for orders.
/// Routes: /api/v1/admin/orders
///
/// Valid status transitions:
///   pending → processing → completed
///   any     → canceled
///   any     → fraud
/// </summary>
[Route("api/v1/admin/orders")]
[Tags("Admin – Orders")]
public class AdminOrderController : AdminBaseController
{
    private readonly DOSDbContext _db;

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "pending", "processing", "completed", "canceled", "closed", "fraud" };

    public AdminOrderController(DOSDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all orders</summary>
    /// <remarks>
    /// Returns all orders with customer info, items and payment method. Supports pagination and filtering.
    ///
    /// **Filter examples:**
    /// - All pending orders: `?status=pending`
    /// - Orders for a specific customer: `?customerId=5`
    /// - Orders placed in June 2026: `?from=2026-06-01&amp;to=2026-06-30`
    /// - Search by order number or customer email: `?search=100024` or `?search=john@example.com`
    ///
    /// **Status values:** `pending` | `processing` | `completed` | `canceled` | `closed` | `fraud`
    /// </remarks>
    /// <param name="page">Page number (starts at 1)</param>
    /// <param name="limit">Results per page (max 100, default 20)</param>
    /// <param name="status">Filter by order status: pending, processing, completed, canceled, closed, fraud</param>
    /// <param name="customerId">Filter by customer ID</param>
    /// <param name="search">Search by order increment ID (e.g. 100024) or customer name/email</param>
    /// <param name="from">Start date filter in ISO format: 2026-01-01</param>
    /// <param name="to">End date filter in ISO format: 2026-06-30</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int    page       = 1,
        [FromQuery] int    limit      = 20,
        [FromQuery] string? status    = null,
        [FromQuery] int?   customerId = null,
        [FromQuery] string? search    = null,
        [FromQuery] string? from      = null,
        [FromQuery] string? to        = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Orders
            .Include(o => o.Payment)
            .Include(o => o.Items.Where(i => i.ParentId == null))
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(o => o.Status == status.ToLower());

        if (customerId.HasValue)
            query = query.Where(o => o.CustomerId == customerId.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o =>
                (o.IncrementId != null && o.IncrementId.Contains(search)) ||
                (o.CustomerEmail != null && o.CustomerEmail.Contains(search)) ||
                (o.CustomerFirstName != null && o.CustomerFirstName.Contains(search)) ||
                (o.CustomerLastName  != null && o.CustomerLastName.Contains(search)));

        if (DateTime.TryParse(from, out var fromDate))
            query = query.Where(o => o.CreatedAt >= fromDate);
        if (DateTime.TryParse(to, out var toDate))
            query = query.Where(o => o.CreatedAt <= toDate.AddDays(1));

        var total  = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        // Status summary counts
        var allStatuses = await _db.Orders
            .GroupBy(o => o.Status)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data    = orders.Select(o => FormatOrderSummary(o)).ToList(),
            meta    = new { total, page, limit, pages = (int)Math.Ceiling(total / (double)limit) },
            status_counts = allStatuses,
        });
    }

    // ─── Get single ───────────────────────────────────────────────────────

    /// <summary>Get full details of a single order</summary>
    /// <remarks>
    /// Returns everything about an order: all items, delivery address, payment method,
    /// invoices, shipments, and any refunds.
    ///
    /// **Use this when:** Admin opens an order to review it in detail or to process a refund/shipment.
    /// </remarks>
    /// <param name="id">The numeric order ID (not the increment ID like "100024" — use the actual database ID)</param>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var order = await _db.Orders
            .AsSplitQuery()
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .Include(o => o.Invoices).ThenInclude(i => i.Items)
            .Include(o => o.Shipments).ThenInclude(s => s.Items)
            .Include(o => o.Refunds).ThenInclude(r => r.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound(new { success = false, message = "Order not found." });

        // Load addresses separately — the Address entity spans customers, orders and carts
        // so EF Core can't resolve it through a simple Include on Order.
        var addresses = await _db.Addresses
            .Where(a => a.OrderId == id)
            .AsNoTracking()
            .ToListAsync();

        return Ok(new { success = true, data = FormatOrderDetail(order, addresses) });
    }

    // ─── Update status ────────────────────────────────────────────────────

    /// <summary>Update an order's status</summary>
    /// <remarks>
    /// Changes the order status. Send JSON body with the new status value.
    ///
    /// **Valid status values:**
    /// - `pending` — Order placed, not yet processed
    /// - `processing` — Order is being prepared
    /// - `completed` — Order delivered successfully
    /// - `canceled` — Order was canceled
    /// - `closed` — Order closed after refund
    /// - `fraud` — Order flagged as fraudulent
    ///
    /// **Example body:**
    /// ```json
    /// { "status": "processing" }
    /// ```
    /// </remarks>
    /// <param name="id">Order database ID</param>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] OrderStatusUpdateRequest req)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        if (string.IsNullOrWhiteSpace(req.Status))
            return BadRequest(new { success = false, message = "status is required." });

        var newStatus = req.Status.Trim().ToLower();
        if (!ValidStatuses.Contains(newStatus))
            return BadRequest(new { success = false, message = $"Invalid status. Valid: {string.Join(", ", ValidStatuses)}" });

        var order = await _db.Orders.FindAsync(id);
        if (order == null) return NotFound(new { success = false, message = "Order not found." });

        var prev      = order.Status;
        order.Status     = newStatus;
        order.UpdatedAt  = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = $"Order #{order.IncrementId} status changed from '{prev}' to '{newStatus}'.", status = newStatus });
    }

    public record OrderStatusUpdateRequest(string Status);

    // ─── Stats overview ───────────────────────────────────────────────────

    /// <summary>Get order revenue and count statistics</summary>
    /// <remarks>
    /// Returns a summary of orders for a date range: total count, total revenue, average order value,
    /// and a breakdown of orders and revenue by status.
    ///
    /// **Use this for:** Admin dashboard charts and KPI cards.
    ///
    /// **Example:** All-time stats — call with no parameters. June 2026 — use `?from=2026-06-01&amp;to=2026-06-30`
    /// </remarks>
    /// <param name="from">Start date in ISO format: 2026-01-01 (optional, defaults to all-time)</param>
    /// <param name="to">End date in ISO format: 2026-06-30 (optional)</param>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] string? from = null,
        [FromQuery] string? to   = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var query = _db.Orders.AsNoTracking();
        if (DateTime.TryParse(from, out var fromDate)) query = query.Where(o => o.CreatedAt >= fromDate);
        if (DateTime.TryParse(to,   out var toDate))   query = query.Where(o => o.CreatedAt <= toDate.AddDays(1));

        var orders = await query.ToListAsync();

        return Ok(new
        {
            success = true,
            data = new
            {
                total_orders    = orders.Count,
                total_revenue   = orders.Sum(o => o.GrandTotal ?? 0),
                avg_order_value = orders.Count > 0 ? orders.Average(o => o.GrandTotal ?? 0) : 0,
                by_status       = orders.GroupBy(o => o.Status)
                    .Select(g => new { status = g.Key, count = g.Count(), revenue = g.Sum(o => o.GrandTotal ?? 0) })
                    .ToList(),
            }
        });
    }

    // ─── Refund management ───────────────────────────────────────────────

    /// <summary>List all customer refund requests</summary>
    /// <remarks>
    /// Returns refund requests submitted by customers. Each entry includes the reason,
    /// the items to be refunded, and the customer's order details.
    ///
    /// **Workflow:**
    /// 1. Customer submits a refund request from the app → state becomes `pending`
    /// 2. Admin reviews it here and either approves or rejects
    /// 3. On approval → state becomes `refunded`, order status becomes `closed`
    /// 4. On rejection → state becomes `rejected`
    ///
    /// **Filter by state:** `?state=pending` to see only requests awaiting action
    /// </remarks>
    /// <param name="state">Filter by state: pending | refunded | rejected</param>
    /// <param name="orderId">Filter by a specific order ID</param>
    /// <param name="page">Page number (starts at 1)</param>
    /// <param name="limit">Results per page (max 100, default 20)</param>
    [HttpGet("/api/v1/admin/refunds")]
    public async Task<IActionResult> ListRefunds(
        [FromQuery] string? state      = null,
        [FromQuery] int?   orderId    = null,
        [FromQuery] int    page       = 1,
        [FromQuery] int    limit      = 20)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Refunds
            .Include(r => r.Items)
            .Include(r => r.Order)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(state))
            query = query.Where(r => r.State == state.ToLower());
        if (orderId.HasValue)
            query = query.Where(r => r.OrderId == orderId.Value);

        var total   = await query.CountAsync();
        var refunds = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data    = refunds.Select(r => FormatRefundSummary(r)).ToList(),
            meta    = new { total, page, limit, pages = (int)Math.Ceiling(total / (double)limit) },
        });
    }

    /// <summary>Approve a customer refund request</summary>
    /// <remarks>
    /// Marks the refund as approved (state → `refunded`) and sets the order status to `closed`.
    /// No request body needed.
    ///
    /// **Use this when:** You have verified the customer's claim and want to process the refund.
    /// The actual money transfer back to the customer must be handled separately in your payment gateway.
    /// </remarks>
    /// <param name="id">The refund ID (from the List Refunds response)</param>
    [HttpPatch("/api/v1/admin/refunds/{id:int}/approve")]
    public async Task<IActionResult> ApproveRefund(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var refund = await _db.Refunds
            .Include(r => r.Items)
            .Include(r => r.Order)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (refund == null) return NotFound(new { success = false, message = "Refund not found." });
        if (refund.State != "pending")
            return BadRequest(new { success = false, message = $"Refund is already '{refund.State}'." });

        refund.State     = "refunded";
        refund.UpdatedAt = DateTime.UtcNow;

        if (refund.Order != null)
        {
            refund.Order.Status     = "closed";
            refund.Order.UpdatedAt  = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Refund approved.", data = FormatRefundSummary(refund) });
    }

    /// <summary>Reject a customer refund request</summary>
    /// <remarks>
    /// Marks the refund as rejected (state → `rejected`). The order status is unchanged.
    /// Request body is optional — you can include a reason but it is not currently stored.
    ///
    /// **Use this when:** The refund request is invalid (e.g. outside return window, policy violation).
    /// </remarks>
    /// <param name="id">The refund ID (from the List Refunds response)</param>
    [HttpPatch("/api/v1/admin/refunds/{id:int}/reject")]
    public async Task<IActionResult> RejectRefund(int id, [FromBody] RejectRefundRequest? req = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var refund = await _db.Refunds.FindAsync(id);
        if (refund == null) return NotFound(new { success = false, message = "Refund not found." });
        if (refund.State != "pending")
            return BadRequest(new { success = false, message = $"Refund is already '{refund.State}'." });

        refund.State     = "rejected";
        refund.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Refund rejected." });
    }

    public record RejectRefundRequest(string? Reason = null);

    private static object FormatRefundSummary(Refund r)
    {
        // Pull reason from the first item's Additional JSON
        string? reason = null;
        var firstItem = r.Items.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstItem?.Additional))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(firstItem.Additional);
                if (doc.RootElement.TryGetProperty("reason", out var el))
                    reason = el.GetString();
            }
            catch { /* ignore */ }
        }

        return new
        {
            id          = r.Id,
            order_id    = r.OrderId,
            state       = r.State,
            grand_total = r.GrandTotal,
            sub_total   = r.SubTotal,
            reason,
            order_increment_id = r.Order?.IncrementId,
            customer_name      = r.Order != null
                ? $"{r.Order.CustomerFirstName} {r.Order.CustomerLastName}".Trim()
                : null,
            customer_email     = r.Order?.CustomerEmail,
            items = r.Items.Select(i => new
            {
                i.Id, i.Name, i.Sku, i.Qty, i.Price, i.Total,
                order_item_id = i.OrderItemId,
            }).ToList(),
            created_at = r.CreatedAt,
            updated_at = r.UpdatedAt,
        };
    }

    // ─── Format helpers ───────────────────────────────────────────────────

    private static object FormatOrderSummary(Order o) => new
    {
        id             = o.Id,
        increment_id   = o.IncrementId,
        status         = o.Status,
        is_guest       = o.IsGuest,
        customer_id    = o.CustomerId,
        customer_name  = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim(),
        customer_email = o.CustomerEmail,
        grand_total    = o.GrandTotal,
        sub_total      = o.SubTotal,
        shipping_amount= o.ShippingAmount,
        discount_amount= o.DiscountAmount,
        coupon_code    = o.CouponCode,
        total_items    = o.TotalItemCount,
        total_qty      = o.TotalQtyOrdered,
        payment_method = o.Payment?.Method,
        currency       = o.OrderCurrencyCode,
        created_at     = o.CreatedAt,
        updated_at     = o.UpdatedAt,
        items          = o.Items
            .Where(i => i.ParentId == null)
            .Select(i => new { i.Id, i.Name, i.Sku, qty = i.QtyOrdered, price = i.Price, total = i.Total })
            .ToList(),
    };

    private static object FormatOrderDetail(Order o, List<Address> addresses) => new
    {
        id             = o.Id,
        increment_id   = o.IncrementId,
        status         = o.Status,
        is_guest       = o.IsGuest,
        customer_id    = o.CustomerId,
        customer_name  = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim(),
        customer_email = o.CustomerEmail,
        grand_total    = o.GrandTotal,
        sub_total      = o.SubTotal,
        tax_amount     = o.TaxAmount,
        shipping_amount= o.ShippingAmount,
        discount_amount= o.DiscountAmount,
        coupon_code    = o.CouponCode,
        shipping_method= o.ShippingTitle,
        total_items    = o.TotalItemCount,
        currency       = o.OrderCurrencyCode,
        payment = o.Payment == null ? null : new
        {
            method       = o.Payment.Method,
            method_title = o.Payment.MethodTitle,
        },
        addresses = addresses.Select(a => new
        {
            a.AddressType, a.FirstName, a.LastName, a.Email, a.Phone,
            address = a.AddressLine, a.City, a.State, a.Postcode, a.Country,
        }).ToList(),
        items = o.Items.Where(i => i.ParentId == null).Select(i => new
        {
            id          = i.Id,
            name        = i.Name,
            sku         = i.Sku,
            qty_ordered = i.QtyOrdered,
            qty_shipped = i.QtyShipped,
            qty_invoiced= i.QtyInvoiced,
            price       = i.Price,
            total       = i.Total,
            tax         = i.TaxAmount,
            discount    = i.DiscountAmount,
        }).ToList(),
        invoices = o.Invoices.Select(inv => new
        {
            id           = inv.Id,
            increment_id = inv.IncrementId,
            state        = inv.State,
            grand_total  = inv.GrandTotal,
            created_at   = inv.CreatedAt,
        }).ToList(),
        shipments = o.Shipments.Select(s => new
        {
            id           = s.Id,
            status       = s.Status,
            track_number = s.TrackNumber,
            carrier      = s.CarrierTitle,
            total_qty    = s.TotalQty,
            created_at   = s.CreatedAt,
        }).ToList(),
        refunds = o.Refunds.Select(r => new
        {
            id          = r.Id,
            state       = r.State,
            grand_total = r.GrandTotal,
            created_at  = r.CreatedAt,
        }).ToList(),
        created_at = o.CreatedAt,
        updated_at = o.UpdatedAt,
    };
}
