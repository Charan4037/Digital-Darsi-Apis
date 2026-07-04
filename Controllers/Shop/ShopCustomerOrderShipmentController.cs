using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-order-shipments")]
[Tags("CustomerOrderShipment")]
[Authorize]
public class ShopCustomerOrderShipmentController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCustomerOrderShipmentController(AccountService accountService)
    {
        _accountService = accountService;
    }

    private static string Fmt(decimal? v) => $"${(v ?? 0):N2}";

    /// <summary>List all customer shipments</summary>
    [HttpGet]
    public async Task<IActionResult> GetShipments()
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var shipments = await _accountService.GetAllShipments(customerId).ToListAsync();

        var data = shipments.Select(s => new
        {
            id = s.Id,
            status = s.Status,
            total_qty = s.TotalQty,
            total_weight = s.TotalWeight,
            carrier_code = s.CarrierCode,
            carrier_title = s.CarrierTitle,
            track_number = s.TrackNumber,
            email_sent = s.EmailSent ? 1 : 0,
            customer_id = s.CustomerId,
            customer_type = s.CustomerType,
            order_id = s.OrderId,
            order_address_id = s.OrderAddressId,
            inventory_source_id = s.InventorySourceId,
            inventory_source_name = s.InventorySourceName,
            created_at = s.CreatedAt,
            updated_at = s.UpdatedAt
        });

        return Ok(data);
    }

    /// <summary>Get shipment detail</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetShipmentDetail(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var shipment = await _accountService.GetShipmentDetailAsync(customerId, id);
        if (shipment == null) return NotFound(new { message = "Shipment not found." });

        return Ok(new
        {
            id = shipment.Id,
            status = shipment.Status,
            total_qty = shipment.TotalQty,
            total_weight = shipment.TotalWeight,
            carrier_code = shipment.CarrierCode,
            carrier_title = shipment.CarrierTitle,
            track_number = shipment.TrackNumber,
            email_sent = shipment.EmailSent ? 1 : 0,
            customer_id = shipment.CustomerId,
            customer_type = shipment.CustomerType,
            order_id = shipment.OrderId,
            order_address_id = shipment.OrderAddressId,
            inventory_source_id = shipment.InventorySourceId,
            inventory_source_name = shipment.InventorySourceName,
            created_at = shipment.CreatedAt,
            updated_at = shipment.UpdatedAt,
            items = shipment.Items.Select(i => new
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
            })
        });
    }
}
