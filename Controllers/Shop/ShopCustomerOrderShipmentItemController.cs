using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer_order_shipment_items")]
[Tags("CustomerOrderShipmentItem")]
[Authorize]
public class ShopCustomerOrderShipmentItemController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCustomerOrderShipmentItemController(AccountService accountService)
    {
        _accountService = accountService;
    }

    private static string Fmt(decimal? v) => $"${(v ?? 0):N2}";

    /// <summary>List all customer shipment items</summary>
    [HttpGet]
    public async Task<IActionResult> GetShipmentItems()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var items = await _accountService.GetShipmentItems(customerId).ToListAsync();

        var data = items.Select(i => new
        {
            id = i.Id,
            name = i.Name,
            description = i.Description,
            sku = i.Sku,
            qty = i.Qty,
            weight = i.Weight,
            price = i.Price ?? 0,
            formatted_price = Fmt(i.Price),
            base_price = i.BasePrice ?? 0,
            formatted_base_price = Fmt(i.BasePrice),
            total = i.Total ?? 0,
            formatted_total = Fmt(i.Total),
            base_total = i.BaseTotal ?? 0,
            formatted_base_total = Fmt(i.BaseTotal),
            product_id = i.ProductId,
            product_type = i.ProductType,
            order_item_id = i.OrderItemId,
            shipment_id = i.ShipmentId
        });

        return Ok(data);
    }

    /// <summary>Get single shipment item</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetShipmentItem(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var item = await _accountService.GetShipmentItems(customerId)
            .FirstOrDefaultAsync(i => i.Id == id);

        if (item == null) return NotFound(new { message = "Shipment item not found." });

        return Ok(new
        {
            id = item.Id,
            name = item.Name,
            description = item.Description,
            sku = item.Sku,
            qty = item.Qty,
            weight = item.Weight,
            price = item.Price ?? 0,
            formatted_price = Fmt(item.Price),
            base_price = item.BasePrice ?? 0,
            formatted_base_price = Fmt(item.BasePrice),
            total = item.Total ?? 0,
            formatted_total = Fmt(item.Total),
            base_total = item.BaseTotal ?? 0,
            formatted_base_total = Fmt(item.BaseTotal),
            product_id = item.ProductId,
            product_type = item.ProductType,
            order_item_id = item.OrderItemId,
            shipment_id = item.ShipmentId
        });
    }
}
