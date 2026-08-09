using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/cancel-order")]
[Tags("CancelOrder")]
[Authorize]
public class ShopCancelOrderController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCancelOrderController(AccountService accountService)
    {
        _accountService = accountService;
    }

    public record CancelOrderRequest(int OrderId);

    /// <summary>Cancel an order</summary>
    [HttpPost]
    public async Task<IActionResult> CancelOrder([FromBody] CancelOrderRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var (success, message) = await _accountService.CancelOrderAsync(customerId, req.OrderId);
        if (!success) return BadRequest(new { message });
        return Ok(new { message });
    }

    public record CancelOrderItemsRequest(int OrderId, List<CancelItemBody> Items);
    public record CancelItemBody(int OrderItemId, int Qty);

    /// <summary>Cancel specific item(s) within an order (not necessarily the whole order)</summary>
    /// <remarks>
    /// Only allowed while the order is still "pending". Send the item's full remaining
    /// quantity to cancel — send every item to get the same effect as canceling the whole
    /// order. Does not create a refund; if the order was paid for online, follow up with a
    /// refund request for the canceled items.
    /// </remarks>
    [HttpPost("items")]
    public async Task<IActionResult> CancelOrderItems([FromBody] CancelOrderItemsRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        if (req.Items == null || req.Items.Count == 0)
            return BadRequest(new { message = "Select at least one item to cancel." });

        var items = req.Items.Select(i => new AccountService.RefundItemRequest(i.OrderItemId, i.Qty)).ToList();
        var (success, message, orderFullyCanceled) = await _accountService.CancelOrderItemsAsync(customerId, req.OrderId, items);
        if (!success) return BadRequest(new { message });
        return Ok(new { message, orderFullyCanceled });
    }
}
