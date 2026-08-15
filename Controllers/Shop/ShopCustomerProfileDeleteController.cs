using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-profile-deletes")]
[Tags("CustomerProfileDelete")]
[Authorize]
public class ShopCustomerProfileDeleteController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCustomerProfileDeleteController(AccountService accountService)
    {
        _accountService = accountService;
    }

    public record DeleteProfileRequest(string? Password = null);

    /// <summary>Delete customer profile</summary>
    [HttpPost("{id:int}")]
    public async Task<IActionResult> DeleteProfile(int id, [FromBody] DeleteProfileRequest req)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var (success, message) = await _accountService.DeleteAccountAsync(id, req.Password);
        if (!success) return BadRequest(new { message });
        return Ok(new { message });
    }
}
