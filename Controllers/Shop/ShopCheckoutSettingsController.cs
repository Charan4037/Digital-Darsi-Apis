using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

/// <summary>
/// Returns checkout configuration (e.g. minimum order value) that the mobile
/// app reads once per cart session so it can enforce the rules client-side
/// before a place-order request hits the server.
///
/// Config is stored in <c>core_config</c> under the key
/// <c>sales.checkout.minimum_order_value</c>. Insert or UPDATE that row from
/// the admin panel (or via SQL) to change the threshold without a code deploy.
/// A missing row or a zero value means no minimum is enforced.
/// </summary>
[ApiController]
[Route("api/shop/checkout-settings")]
[Tags("CheckoutSettings")]
[AllowAnonymous]
public class ShopCheckoutSettingsController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopCheckoutSettingsController(BagistoDbContext db)
    {
        _db = db;
    }

    internal const string MinOrderKey = "sales.checkout.minimum_order_value";

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var row = await _db.CoreConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == MinOrderKey);

        var minValue = 0m;
        if (row?.Value != null && decimal.TryParse(row.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            minValue = parsed;

        return Ok(new
        {
            data = new
            {
                minimum_order_value = minValue
            }
        });
    }
}
