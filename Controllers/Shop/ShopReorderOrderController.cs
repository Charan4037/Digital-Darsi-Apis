using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/reorder")]
[Tags("ReorderOrder")]
[Authorize]
public class ShopReorderOrderController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopReorderOrderController(AccountService accountService)
    {
        _accountService = accountService;
    }

    public record ReorderRequest(int OrderId);

    /// <summary>Reorder - add items from a previous order to cart</summary>
    [HttpPost]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customerId")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var (success, message, orderId, itemsAddedCount) = await _accountService.ReorderAsync(customerId, req.OrderId);
        if (!success) return BadRequest(new { message });
        return Ok(new { message });
    }
}
