using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;
using static DOSApi.Services.AccountService;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Authorize]
[Tags("Refunds")]
public class ShopRefundController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopRefundController(AccountService accountService)
    {
        _accountService = accountService;
    }

    private int GetCustomerId()
    {
        var claim = User.FindFirst("customer_id")?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    /// <summary>
    /// Request a refund for an order (must be completed or processing).
    /// Pass an optional list of specific items to refund; omitting items means refund the full order.
    /// </summary>
    [HttpPost("api/v1/customer/orders/{orderId:int}/refund-request")]
    public async Task<IActionResult> RequestRefund(int orderId, [FromBody] RefundRequestBody req)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { success = false, message = "reason is required." });

        var itemRequests = req.Items?.Select(i => new RefundItemRequest(i.OrderItemId, i.Qty)).ToList();
        var (success, message, refund) = await _accountService.RequestRefundAsync(
            customerId, orderId, req.Reason, itemRequests);

        if (!success) return BadRequest(new { success = false, message });
        return Ok(new
        {
            success = true,
            message,
            data = new
            {
                refund_id   = refund!.Id,
                state       = refund.State,
                grand_total = refund.GrandTotal,
                created_at  = refund.CreatedAt,
            },
        });
    }

    /// <summary>Get the refund status for a specific order.</summary>
    [HttpGet("api/v1/customer/orders/{orderId:int}/refund")]
    public async Task<IActionResult> GetRefund(int orderId)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var refund = await _accountService.GetOrderRefundAsync(customerId, orderId);
        if (refund == null) return NotFound(new { success = false, message = "No refund found for this order." });

        // Pull the reason from the first item's Additional JSON
        string? reason = null;
        var firstItem = refund.Items.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstItem?.Additional))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(firstItem.Additional);
                reason = doc.RootElement.GetProperty("reason").GetString();
            }
            catch { /* ignore malformed JSON */ }
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                id          = refund.Id,
                order_id    = refund.OrderId,
                state       = refund.State,
                grand_total = refund.GrandTotal,
                sub_total   = refund.SubTotal,
                reason,
                items = refund.Items.Select(i => new
                {
                    i.Id, i.Name, i.Sku, i.Qty, i.Price, i.Total,
                    order_item_id = i.OrderItemId,
                }),
                created_at = refund.CreatedAt,
                updated_at = refund.UpdatedAt,
            },
        });
    }

    public record RefundRequestBody(string Reason, List<RefundItemBody>? Items = null);
    public record RefundItemBody(int OrderItemId, int Qty);
}
