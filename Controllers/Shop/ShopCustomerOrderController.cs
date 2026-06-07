using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-orders")]
[Tags("CustomerOrder")]
[Authorize]
public class ShopCustomerOrderController : ControllerBase
{
    private readonly AccountService _accountService;
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopCustomerOrderController(AccountService accountService, BagistoDbContext db, IConfiguration config)
    {
        _accountService = accountService;
        _db = db;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    private static string Fmt(decimal? v) => $"${(v ?? 0):N2}";

    /// <summary>List customer orders</summary>
    [HttpGet]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string? status)
    {
        var customerId = int.Parse(User.FindFirst("customer_id")?.Value ?? "0");
        if (customerId == 0) return Unauthorized();

        var orders = await _accountService.GetOrders(customerId, status)
            .Include(o => o.Items)
            .ToListAsync();

        // Batch-load images for all products across all orders in one query
        var allProductIds = orders
            .SelectMany(o => o.Items)
            .Where(i => i.ProductId.HasValue)
            .Select(i => i.ProductId!.Value)
            .Distinct()
            .ToList();

        var imagesByProductId = allProductIds.Count > 0
            ? await _db.ProductImages
                .Where(pi => allProductIds.Contains(pi.ProductId))
                .GroupBy(pi => pi.ProductId)
                .Select(g => g.OrderBy(pi => pi.Position).First())
                .ToDictionaryAsync(pi => pi.ProductId, pi => pi.Path)
            : new Dictionary<int, string>();

        var data = orders.Select(o => new
        {
            id = o.Id,
            increment_id = o.IncrementId,
            status = o.Status,
            channel_name = o.ChannelName,
            is_guest = (o.IsGuest == true) ? 1 : 0,
            customer_email = o.CustomerEmail,
            customer_first_name = o.CustomerFirstName,
            customer_last_name = o.CustomerLastName,
            shipping_method = o.ShippingMethod,
            shipping_title = o.ShippingTitle,
            shipping_description = o.ShippingDescription,
            coupon_code = o.CouponCode,
            is_gift = o.IsGift ? 1 : 0,
            total_item_count = o.TotalItemCount,
            total_qty_ordered = o.TotalQtyOrdered,
            base_currency_code = o.BaseCurrencyCode,
            channel_currency_code = o.ChannelCurrencyCode,
            order_currency_code = o.OrderCurrencyCode,
            grand_total = o.GrandTotal ?? 0,
            formatted_grand_total = Fmt(o.GrandTotal),
            base_grand_total = o.BaseGrandTotal ?? 0,
            formatted_base_grand_total = Fmt(o.BaseGrandTotal),
            sub_total = o.SubTotal ?? 0,
            formatted_sub_total = Fmt(o.SubTotal),
            base_sub_total = o.BaseSubTotal ?? 0,
            formatted_base_sub_total = Fmt(o.BaseSubTotal),
            tax_amount = o.TaxAmount ?? 0,
            formatted_tax_amount = Fmt(o.TaxAmount),
            base_tax_amount = o.BaseTaxAmount ?? 0,
            shipping_amount = o.ShippingAmount ?? 0,
            formatted_shipping_amount = Fmt(o.ShippingAmount),
            discount_amount = o.DiscountAmount ?? 0,
            formatted_discount_amount = Fmt(o.DiscountAmount),
            created_at = o.CreatedAt,
            updated_at = o.UpdatedAt,
            items = o.Items.Select(i => new
            {
                id = i.Id,
                product_id = i.ProductId,
                name = i.Name,
                qty_ordered = i.QtyOrdered,
                price = i.Price ?? 0,
                total = i.Total ?? 0,
                image_url = i.ProductId.HasValue && imagesByProductId.TryGetValue(i.ProductId.Value, out var p)
                    ? ResolveSmallImageUrl(p)
                    : null,
            })
        });

        return Ok(data);
    }

    /// <summary>Get order detail with items, payment, and addresses</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOrderDetail(int id)
    {
        if (!int.TryParse(User.FindFirst("customer_id")?.Value, out var customerId) || customerId == 0)
            return Unauthorized();

        var order = await _accountService.GetOrderDetailAsync(customerId, id);
        if (order == null) return NotFound(new { message = "Order not found." });

        var addresses = await _accountService.GetOrderAddressesAsync(id);
        var billing = addresses.FirstOrDefault(a => a.AddressType == "order_billing");
        var shipping = addresses.FirstOrDefault(a => a.AddressType == "order_shipping");

        // Fetch the first product image for each item in one query
        var productIds = order.Items
            .Where(i => i.ProductId.HasValue)
            .Select(i => i.ProductId!.Value)
            .Distinct()
            .ToList();

        var imagesByProductId = await _db.ProductImages
            .Where(pi => productIds.Contains(pi.ProductId))
            .GroupBy(pi => pi.ProductId)
            .Select(g => g.OrderBy(pi => pi.Position).First())
            .ToDictionaryAsync(pi => pi.ProductId, pi => pi.Path);

        return Ok(new
        {
            id = order.Id,
            increment_id = order.IncrementId,
            status = order.Status,
            channel_name = order.ChannelName,
            is_guest = (order.IsGuest == true) ? 1 : 0,
            customer_email = order.CustomerEmail,
            customer_first_name = order.CustomerFirstName,
            customer_last_name = order.CustomerLastName,
            shipping_method = order.ShippingMethod,
            shipping_title = order.ShippingTitle,
            shipping_description = order.ShippingDescription,
            coupon_code = order.CouponCode,
            is_gift = order.IsGift ? 1 : 0,
            total_item_count = order.TotalItemCount,
            total_qty_ordered = order.TotalQtyOrdered,
            base_currency_code = order.BaseCurrencyCode,
            channel_currency_code = order.ChannelCurrencyCode,
            order_currency_code = order.OrderCurrencyCode,
            grand_total = order.GrandTotal ?? 0,
            formatted_grand_total = Fmt(order.GrandTotal),
            base_grand_total = order.BaseGrandTotal ?? 0,
            formatted_base_grand_total = Fmt(order.BaseGrandTotal),
            sub_total = order.SubTotal ?? 0,
            formatted_sub_total = Fmt(order.SubTotal),
            base_sub_total = order.BaseSubTotal ?? 0,
            formatted_base_sub_total = Fmt(order.BaseSubTotal),
            tax_amount = order.TaxAmount ?? 0,
            formatted_tax_amount = Fmt(order.TaxAmount),
            base_tax_amount = order.BaseTaxAmount ?? 0,
            shipping_amount = order.ShippingAmount ?? 0,
            formatted_shipping_amount = Fmt(order.ShippingAmount),
            discount_amount = order.DiscountAmount ?? 0,
            formatted_discount_amount = Fmt(order.DiscountAmount),
            created_at = order.CreatedAt,
            updated_at = order.UpdatedAt,
            items = order.Items.Select(i =>
            {
                var imagePath = i.ProductId.HasValue && imagesByProductId.TryGetValue(i.ProductId.Value, out var p) ? p : null;
                var imageUrl = ResolveSmallImageUrl(imagePath);
                return new
                {
                    id = i.Id,
                    sku = i.Sku,
                    type = i.Type,
                    name = i.Name,
                    product_id = i.ProductId,
                    image_url = imageUrl,
                    qty_ordered = i.QtyOrdered,
                    qty_shipped = i.QtyShipped,
                    qty_invoiced = i.QtyInvoiced,
                    qty_canceled = i.QtyCanceled,
                    qty_refunded = i.QtyRefunded,
                    price = i.Price ?? 0,
                    formatted_price = Fmt(i.Price),
                    base_price = i.BasePrice ?? 0,
                    formatted_base_price = Fmt(i.BasePrice),
                    total = i.Total ?? 0,
                    formatted_total = Fmt(i.Total),
                    base_total = i.BaseTotal ?? 0,
                    formatted_base_total = Fmt(i.BaseTotal),
                    tax_amount = i.TaxAmount ?? 0,
                    formatted_tax_amount = Fmt(i.TaxAmount),
                    tax_percent = i.TaxPercent,
                    discount_amount = i.DiscountAmount ?? 0,
                    formatted_discount_amount = Fmt(i.DiscountAmount),
                    discount_percent = i.DiscountPercent,
                    additional = i.Additional,
                    created_at = i.CreatedAt,
                    updated_at = i.UpdatedAt
                };
            }),
            billing_address = billing != null ? MapAddress(billing) : null,
            shipping_address = shipping != null ? MapAddress(shipping) : null,
            payment = order.Payment != null ? new
            {
                id = order.Payment.Id,
                method = order.Payment.Method,
                method_title = order.Payment.MethodTitle
            } : null
        });
    }

    private string? ResolveSmallImageUrl(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        return $"{_baseUrl}/cache/small/{path}";
    }

    private static object MapAddress(Models.Customer.Address a) => new
    {
        id = a.Id,
        address_type = a.AddressType,
        first_name = a.FirstName,
        last_name = a.LastName,
        company_name = a.CompanyName,
        address = (a.AddressLine ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries),
        city = a.City,
        state = a.State,
        country = a.Country,
        postcode = a.Postcode,
        email = a.Email,
        phone = a.Phone
    };
}
