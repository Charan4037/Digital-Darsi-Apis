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
}
