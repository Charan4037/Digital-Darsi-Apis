using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Sales;
using DOSApi.Models.Customer;
using DOSApi.Services;

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
[Route("api/admin/orders")] // some app clients call without the /v1/ segment — accept both
[Tags("Admin – Orders")]
public class AdminOrderController : AdminBaseController
{
    private readonly DOSDbContext _db;

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "pending", "processing", "completed", "canceled", "closed", "fraud" };

    private readonly OrderInvoiceService _invoiceService;
    private readonly AccountService _accountService;

    public AdminOrderController(DOSDbContext db, OrderInvoiceService invoiceService, AccountService accountService, IConfiguration config) : base(db, config)
    {
        _db = db;
        _invoiceService = invoiceService;
        _accountService = accountService;
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
        if (!await HasPermissionAsync("orders")) return AdminUnauthorized();
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

        // Order.CustomerFirstName/LastName are sometimes blank (a checkout-flow gap that
        // predates the fix in CheckoutService) — fall back to the shipping/billing
        // address name, which is always populated, for those orders.
        var orderIds = orders.Select(o => o.Id).ToList();
        var nameFallbackAddresses = await _db.Addresses
            .Where(a => a.OrderId != null && orderIds.Contains(a.OrderId.Value)
                && (a.AddressType == "order_shipping" || a.AddressType == "order_billing"))
            .AsNoTracking()
            .ToListAsync();

        var data = orders.Select(o =>
        {
            var customerName = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim();
            if (string.IsNullOrWhiteSpace(customerName))
            {
                var addr = nameFallbackAddresses.FirstOrDefault(a => a.OrderId == o.Id && a.AddressType == "order_shipping")
                    ?? nameFallbackAddresses.FirstOrDefault(a => a.OrderId == o.Id);
                if (addr != null) customerName = $"{addr.FirstName} {addr.LastName}".Trim();
            }
            return FormatOrderSummary(o, customerName);
        }).ToList();

        return Ok(new
        {
            success = true,
            data,
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
        if (!await HasPermissionAsync("orders")) return AdminUnauthorized();

        var order = await _db.Orders
            .AsSplitQuery()
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .Include(o => o.ExtraCharges)
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
        if (!await HasPermissionAsync("orders", requireWrite: true)) return AdminForbidden("orders");

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
        if (newStatus == "completed") order.DeliveredAt ??= DateTime.UtcNow;
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
        if (!await HasPermissionAsync("orders")) return AdminUnauthorized();

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
    /// 3. On approval, admin confirms whether the payment has already been sent:
    ///    - Yes → state becomes `refunded`, order status becomes `closed`
    ///    - Not yet → state becomes `approved` (payment still owed) — call
    ///      `/api/v1/admin/refunds/{id}/mark-paid` once the money is actually sent
    /// 4. On rejection → state becomes `rejected`
    ///
    /// **Filter by state:** `?state=pending` to see only requests awaiting action
    /// </remarks>
    /// <param name="state">Filter by state: pending | approved | refunded | rejected</param>
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
        if (!await HasPermissionAsync("refunds")) return AdminUnauthorized();
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
    /// Marks the refund as approved. You must confirm whether the payment has already been
    /// sent to the customer:
    /// - `paymentCompleted: true` → state becomes `refunded`, order status becomes `closed`,
    ///   and the order's/order items' refunded-total columns are updated so reporting (e.g.
    ///   the Transactions screen) reflects the payout.
    /// - `paymentCompleted: false` → state becomes `approved` — the claim is accepted but the
    ///   money hasn't gone out yet. The order isn't closed and totals aren't updated until you
    ///   later call `/api/v1/admin/refunds/{id}/mark-paid` once you've actually sent it.
    ///
    /// This endpoint only records what happened — it does not itself call any payment gateway
    /// to move money.
    /// </remarks>
    /// <param name="id">The refund ID (from the List Refunds response)</param>
    /// <param name="req">Whether the payment has already been sent to the customer</param>
    [HttpPatch("/api/v1/admin/refunds/{id:int}/approve")]
    public async Task<IActionResult> ApproveRefund(int id, [FromBody] ApproveRefundRequest req)
    {
        if (!await HasPermissionAsync("refunds", requireWrite: true)) return AdminForbidden("refunds");

        var refund = await _db.Refunds
            .Include(r => r.Items)
            .Include(r => r.Order).ThenInclude(o => o!.Items)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (refund == null) return NotFound(new { success = false, message = "Refund not found." });
        if (refund.State != "pending")
            return BadRequest(new { success = false, message = $"Refund is already '{refund.State}'." });

        string message;
        if (req.PaymentCompleted)
        {
            ApplyApprovalEffects(refund);
            message = "Refund approved and marked as paid.";
        }
        else
        {
            refund.State     = "approved";
            refund.UpdatedAt = DateTime.UtcNow;
            message = "Refund approved — payment still pending. Mark it as paid once you've sent the money.";
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message, data = FormatRefundSummary(refund) });
    }

    public record ApproveRefundRequest(bool PaymentCompleted = false);

    /// <summary>Confirm payment for a previously-approved refund</summary>
    /// <remarks>
    /// For a refund that was approved without confirming payment yet (state `approved`) — call
    /// this once the money has actually been sent to the customer. Marks the refund `refunded`,
    /// closes the order, and folds the amount into the order's/order items' refunded totals.
    /// </remarks>
    /// <param name="id">The refund ID (from the List Refunds response)</param>
    [HttpPatch("/api/v1/admin/refunds/{id:int}/mark-paid")]
    public async Task<IActionResult> MarkRefundPaid(int id)
    {
        if (!await HasPermissionAsync("refunds", requireWrite: true)) return AdminForbidden("refunds");

        var refund = await _db.Refunds
            .Include(r => r.Items)
            .Include(r => r.Order).ThenInclude(o => o!.Items)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (refund == null) return NotFound(new { success = false, message = "Refund not found." });
        if (refund.State != "approved")
            return BadRequest(new { success = false, message = $"Only an 'approved' refund awaiting payment can be marked paid — this one is '{refund.State}'." });

        ApplyApprovalEffects(refund);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Refund marked as paid.", data = FormatRefundSummary(refund) });
    }

    /// <summary>Create a refund for an order (admin-initiated)</summary>
    /// <remarks>
    /// Unlike the customer-facing refund-request flow, this lets an admin issue a refund for
    /// ANY order at any time — e.g. right after admin-canceling an order for a stock/fraud/
    /// fulfillment issue, or to make a customer whole without waiting for them to submit a
    /// request. You must confirm whether the payment has already been sent to the customer:
    /// - `paymentCompleted: true` → marked paid out immediately, order status becomes `closed`.
    /// - `paymentCompleted: false` → created as `approved` (payment still owed) — call
    ///   `/api/v1/admin/refunds/{id}/mark-paid` once you've actually sent the money.
    /// This does not itself call any payment gateway to move money.
    ///
    /// Only one *open* refund is allowed per order — if one is already pending review, approved
    /// and awaiting payment, or already paid out, this returns a 409 telling you to use
    /// Approve/Reject/Mark Paid on it instead. A previously rejected refund does not block a
    /// fresh admin-initiated one.
    /// </remarks>
    /// <param name="id">Order database ID</param>
    /// <param name="req">Reason, optional item selection, and whether the payment has already been sent</param>
    [HttpPost("{id:int}/refund")]
    public async Task<IActionResult> CreateAdminRefund(int id, [FromBody] CreateAdminRefundRequest req)
    {
        if (!await HasPermissionAsync("refunds", requireWrite: true)) return AdminForbidden("refunds");

        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { success = false, message = "reason is required." });

        var itemRequests = req.Items?.Select(i => new AccountService.RefundItemRequest(i.OrderItemId, i.Qty)).ToList();
        var (success, message, created) = await _accountService.CreateAdminRefundAsync(id, req.Reason, itemRequests);
        if (!success || created == null)
            return Conflict(new { success = false, message });

        var refund = await _db.Refunds
            .Include(r => r.Items)
            .Include(r => r.Order).ThenInclude(o => o!.Items)
            .FirstAsync(r => r.Id == created.Id);

        string resultMessage;
        if (req.PaymentCompleted)
        {
            ApplyApprovalEffects(refund);
            resultMessage = "Refund created and marked as paid.";
        }
        else
        {
            refund.State     = "approved";
            refund.UpdatedAt = DateTime.UtcNow;
            resultMessage = "Refund created — payment still pending. Mark it as paid once you've sent the money.";
        }
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = resultMessage, data = FormatRefundSummary(refund) });
    }

    public record CreateAdminRefundRequest(string Reason, bool PaymentCompleted = false, List<CreateAdminRefundItem>? Items = null);
    public record CreateAdminRefundItem(int OrderItemId, int Qty);

    /// <summary>Shared by ApproveRefund and CreateAdminRefund — marks the refund paid out,
    /// closes its order, and folds the amount/quantities into the order's/order items'
    /// refunded-total columns so reporting reflects the payout.</summary>
    private static void ApplyApprovalEffects(Refund refund)
    {
        refund.State     = "refunded";
        refund.UpdatedAt = DateTime.UtcNow;

        var order = refund.Order;
        if (order == null) return;

        order.Status                 = "closed";
        order.UpdatedAt              = DateTime.UtcNow;
        order.GrandTotalRefunded     = (order.GrandTotalRefunded ?? 0) + (refund.GrandTotal ?? 0);
        order.BaseGrandTotalRefunded = (order.BaseGrandTotalRefunded ?? 0) + (refund.BaseGrandTotal ?? 0);
        order.SubTotalRefunded       = (order.SubTotalRefunded ?? 0) + (refund.SubTotal ?? 0);
        order.BaseSubTotalRefunded   = (order.BaseSubTotalRefunded ?? 0) + (refund.BaseSubTotal ?? 0);

        foreach (var refundItem in refund.Items)
        {
            if (refundItem.OrderItemId == null) continue;
            var orderItem = order.Items.FirstOrDefault(oi => oi.Id == refundItem.OrderItemId.Value);
            if (orderItem != null)
                orderItem.QtyRefunded = (orderItem.QtyRefunded ?? 0) + (refundItem.Qty ?? 0);
        }
    }

    /// <summary>Reject a customer refund request</summary>
    /// <remarks>
    /// Marks the refund as rejected (state → `rejected`). The order status is unchanged.
    /// An optional reason is saved and returned in the refund's `rejection_reason` field.
    ///
    /// **Use this when:** The refund request is invalid (e.g. outside return window, policy violation).
    /// </remarks>
    /// <param name="id">The refund ID (from the List Refunds response)</param>
    [HttpPatch("/api/v1/admin/refunds/{id:int}/reject")]
    public async Task<IActionResult> RejectRefund(int id, [FromBody] RejectRefundRequest? req = null)
    {
        if (!await HasPermissionAsync("refunds", requireWrite: true)) return AdminForbidden("refunds");

        var refund = await _db.Refunds
            .Include(r => r.Items)
            .Include(r => r.Order)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (refund == null) return NotFound(new { success = false, message = "Refund not found." });
        if (refund.State != "pending")
            return BadRequest(new { success = false, message = $"Refund is already '{refund.State}'." });

        refund.State     = "rejected";
        refund.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(req?.Reason))
        {
            var firstItem = refund.Items.FirstOrDefault();
            if (firstItem != null)
                firstItem.Additional = MergeAdditionalJson(firstItem.Additional, "rejection_reason", req!.Reason!.Trim());
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Refund rejected.", data = FormatRefundSummary(refund) });
    }

    public record RejectRefundRequest(string? Reason = null);

    /// <summary>Merges one key into the existing JSON object stored in a RefundItem's
    /// Additional column, preserving whatever other keys (e.g. the original request
    /// "reason") are already there. Falls back to a fresh object if the existing value
    /// is empty or malformed.</summary>
    private static string MergeAdditionalJson(string? existingJson, string key, string value)
    {
        var dict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(existingJson))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(existingJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.GetString() ?? "";
            }
            catch { /* start fresh on malformed existing JSON */ }
        }
        dict[key] = value;
        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    private static object FormatRefundSummary(Refund r)
    {
        // Pull reason/rejection_reason from the first item's Additional JSON
        string? reason = null;
        string? rejectionReason = null;
        var firstItem = r.Items.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstItem?.Additional))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(firstItem.Additional);
                if (doc.RootElement.TryGetProperty("reason", out var el))
                    reason = el.GetString();
                if (doc.RootElement.TryGetProperty("rejection_reason", out var rel))
                    rejectionReason = rel.GetString();
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
            rejection_reason = rejectionReason,
            order_increment_id = r.Order?.IncrementId,
            order_status       = r.Order?.Status,
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

    private static object FormatOrderSummary(Order o, string customerName) => new
    {
        id             = o.Id,
        increment_id   = o.IncrementId,
        status         = o.Status,
        is_guest       = o.IsGuest,
        customer_id    = o.CustomerId,
        customer_name  = customerName,
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

    private static object FormatOrderDetail(Order o, List<Address> addresses)
    {
        // Order.CustomerFirstName/LastName are sometimes blank (a checkout-flow gap that
        // predates the fix in CheckoutService) — fall back to the shipping/billing
        // address name, which is always populated, for those orders.
        var customerName = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim();
        if (string.IsNullOrWhiteSpace(customerName))
        {
            var addr = addresses.FirstOrDefault(a => a.AddressType == "order_shipping")
                ?? addresses.FirstOrDefault(a => a.AddressType == "order_billing")
                ?? addresses.FirstOrDefault();
            if (addr != null) customerName = $"{addr.FirstName} {addr.LastName}".Trim();
        }

        return new
    {
        id             = o.Id,
        increment_id   = o.IncrementId,
        status         = o.Status,
        is_guest       = o.IsGuest,
        customer_id    = o.CustomerId,
        customer_name  = customerName,
        customer_email = o.CustomerEmail,
        customer_phone = addresses.FirstOrDefault(a => a.AddressType == "order_shipping")?.Phone
                          ?? addresses.FirstOrDefault(a => a.AddressType == "order_billing")?.Phone
                          ?? addresses.FirstOrDefault()?.Phone,
        grand_total    = o.GrandTotal,
        sub_total      = o.SubTotal,
        tax_amount     = o.TaxAmount,
        shipping_amount= o.ShippingAmount,
        discount_amount= o.DiscountAmount,
        extra_charges_total = o.ExtraChargesTotal,
        extra_charges  = o.ExtraCharges
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.ChargeType, c.Rate, c.Amount })
            .ToList(),
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

    /// <summary>Download order invoice as PDF (admin access, cached for 10 minutes)</summary>
    [HttpGet("{id:int}/invoice")]
    public async Task<IActionResult> DownloadInvoice(int id)
    {
        // Verify admin has read access to orders
        if (!await HasPermissionAsync("orders"))
            return AdminUnauthorized();

        try
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
            if (order == null)
                return NotFound(new { message = $"Order {id} not found." });

            // Generate invoice for ANY customer (admins can download any invoice, including
            // guest orders where CustomerId is null — no ownership check applies here).
            try
            {
                var pdfBytes = await _invoiceService.GenerateInvoicePdfAsync(id);
                if (pdfBytes == null || pdfBytes.Length == 0)
                    return BadRequest(new { message = "Failed to generate invoice PDF - empty result." });

                return File(pdfBytes, "application/pdf", $"invoice_order_{id}.pdf");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = $"Invoice generation failed: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error generating invoice", error = ex.Message, type = ex.GetType().Name });
            }
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "Order not found." });
        }
    }
}
