using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-invoices")]
[Tags("CustomerInvoice")]
[Authorize]
public class ShopCustomerInvoiceController : ControllerBase
{
    private readonly AccountService _accountService;

    public ShopCustomerInvoiceController(AccountService accountService)
    {
        _accountService = accountService;
    }

    private static string Fmt(decimal? v) => $"${(v ?? 0):N2}";

    /// <summary>List customer invoices</summary>
    [HttpGet]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] int? orderId,
        [FromQuery] string? state)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var invoices = await _accountService.GetInvoices(customerId, orderId, state).ToListAsync();

        var data = invoices.Select(i => new
        {
            id = i.Id,
            increment_id = i.IncrementId,
            state = i.State,
            email_sent = i.EmailSent ? 1 : 0,
            total_qty = i.TotalQty,
            base_currency_code = i.BaseCurrencyCode,
            channel_currency_code = i.ChannelCurrencyCode,
            order_currency_code = i.OrderCurrencyCode,
            sub_total = i.SubTotal ?? 0,
            formatted_sub_total = Fmt(i.SubTotal),
            base_sub_total = i.BaseSubTotal ?? 0,
            formatted_base_sub_total = Fmt(i.BaseSubTotal),
            grand_total = i.GrandTotal ?? 0,
            formatted_grand_total = Fmt(i.GrandTotal),
            base_grand_total = i.BaseGrandTotal ?? 0,
            formatted_base_grand_total = Fmt(i.BaseGrandTotal),
            shipping_amount = i.ShippingAmount ?? 0,
            formatted_shipping_amount = Fmt(i.ShippingAmount),
            tax_amount = i.TaxAmount ?? 0,
            formatted_tax_amount = Fmt(i.TaxAmount),
            discount_amount = i.DiscountAmount ?? 0,
            formatted_discount_amount = Fmt(i.DiscountAmount),
            order_id = i.OrderId,
            created_at = i.CreatedAt,
            updated_at = i.UpdatedAt
        });

        return Ok(data);
    }

    /// <summary>Get invoice detail with items</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetInvoiceDetail(int id)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var invoice = await _accountService.GetInvoiceDetailAsync(customerId, id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });

        return Ok(new
        {
            id = invoice.Id,
            increment_id = invoice.IncrementId,
            state = invoice.State,
            email_sent = invoice.EmailSent ? 1 : 0,
            total_qty = invoice.TotalQty,
            base_currency_code = invoice.BaseCurrencyCode,
            channel_currency_code = invoice.ChannelCurrencyCode,
            order_currency_code = invoice.OrderCurrencyCode,
            sub_total = invoice.SubTotal ?? 0,
            formatted_sub_total = Fmt(invoice.SubTotal),
            base_sub_total = invoice.BaseSubTotal ?? 0,
            formatted_base_sub_total = Fmt(invoice.BaseSubTotal),
            grand_total = invoice.GrandTotal ?? 0,
            formatted_grand_total = Fmt(invoice.GrandTotal),
            base_grand_total = invoice.BaseGrandTotal ?? 0,
            formatted_base_grand_total = Fmt(invoice.BaseGrandTotal),
            shipping_amount = invoice.ShippingAmount ?? 0,
            formatted_shipping_amount = Fmt(invoice.ShippingAmount),
            tax_amount = invoice.TaxAmount ?? 0,
            formatted_tax_amount = Fmt(invoice.TaxAmount),
            discount_amount = invoice.DiscountAmount ?? 0,
            formatted_discount_amount = Fmt(invoice.DiscountAmount),
            order_id = invoice.OrderId,
            created_at = invoice.CreatedAt,
            updated_at = invoice.UpdatedAt,
            items = invoice.Items.Select(i => new
            {
                id = i.Id,
                name = i.Name,
                sku = i.Sku,
                qty = i.Qty,
                price = i.Price ?? 0,
                formatted_price = Fmt(i.Price),
                base_price = i.BasePrice ?? 0,
                formatted_base_price = Fmt(i.BasePrice),
                total = i.Total ?? 0,
                formatted_total = Fmt(i.Total),
                base_total = i.BaseTotal ?? 0,
                tax_amount = i.TaxAmount ?? 0,
                formatted_tax_amount = Fmt(i.TaxAmount),
                base_tax_amount = i.BaseTaxAmount ?? 0,
                discount_amount = i.DiscountAmount ?? 0,
                formatted_discount_amount = Fmt(i.DiscountAmount),
                product_id = i.ProductId,
                additional = i.Additional,
                created_at = i.CreatedAt
            })
        });
    }
}
