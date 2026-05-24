using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/delete-all-compare-items")]
[Tags("DeleteAllCompareItems")]
[Authorize]
public class ShopDeleteAllCompareItemsController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopDeleteAllCompareItemsController(BagistoDbContext db)
    {
        _db = db;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customer_id")?.Value ?? "0");

    /// <summary>Clear all compare items for the current customer</summary>
    [HttpPost]
    public async Task<IActionResult> DeleteAllCompareItems()
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var items = await _db.CompareItems
            .Where(ci => ci.CustomerId == customerId)
            .ToListAsync();

        if (items.Count == 0)
            return Ok(new { message = "Compare list is already empty." });

        _db.CompareItems.RemoveRange(items);
        await _db.SaveChangesAsync();

        return Ok(new { message = "All compare items removed successfully." });
    }
}
