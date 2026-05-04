using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-address-deletes")]
[Tags("DeleteCustomerAddress")]
[Authorize]
public class ShopDeleteCustomerAddressController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopDeleteCustomerAddressController(AccountService accountService)
    {
        _accountService = accountService;
    }

    /// <summary>Delete a customer address</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAddress(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var (success, message) = await _accountService.DeleteAddressAsync(customerId, id);
        if (!success) return NotFound(new { message });
        return Ok(new { message });
    }
}
