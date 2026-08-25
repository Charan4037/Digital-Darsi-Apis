using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Helpers;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/customer-orders")]
[Tags("CustomerOrder")]
[Authorize]
public class ShopCustomerOrderController : ControllerBase
{
    private readonly AccountService _accountService;
    private readonly DOSDbContext _db;
    private readonly OrderInvoiceService _invoiceService;
    private readonly ProductService _productService;
    private readonly string _baseUrl;

    public ShopCustomerOrderController(AccountService accountService, DOSDbContext db, OrderInvoiceService invoiceService, ProductService productService, IConfiguration config)
    {
        _accountService = accountService;
        _db = db;
        _invoiceService = invoiceService;
        _productService = productService;
        _baseUrl = (config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
    }

    private static string Fmt(decimal? v) => PriceFormatter.Format(v ?? 0);

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

        var imagesByProductId = new Dictionary<int, string>();
        if (allProductIds.Count > 0)
        {
            // Variant children carry no images — images live on the parent product.
            var parentMap = await _db.Products
                .Where(p => allProductIds.Contains(p.Id) && p.ParentId != null)
                .Select(p => new { p.Id, p.ParentId })
                .AsNoTracking()
                .ToDictionaryAsync(p => p.Id, p => p.ParentId!.Value);

            var imageIds = allProductIds
                .Select(id => parentMap.TryGetValue(id, out var pid) ? pid : id)
                .Distinct()
                .ToList();

            var pathById = (await _db.ProductImages
                .Where(pi => imageIds.Contains(pi.ProductId))
                .OrderBy(pi => pi.Position)
                .ToListAsync())
                .GroupBy(pi => pi.ProductId)
                .ToDictionary(g => g.Key, g => g.First().Path);

            foreach (var id in allProductIds)
            {
                var lookupId = parentMap.TryGetValue(id, out var pid) ? pid : id;
                if (pathById.TryGetValue(lookupId, out var path))
                    imagesByProductId[id] = path;
            }
        }

        var urlKeyByProductId = new Dictionary<int, string?>();
        if (allProductIds.Count > 0)
        {
            var orderedProducts = await _db.Products
                .Where(p => allProductIds.Contains(p.Id))
                .Include(p => p.AttributeValues)
                .Include(p => p.Flats)
                .AsNoTracking()
                .ToListAsync();

            urlKeyByProductId = orderedProducts
                .ToDictionary(p => p.Id, p => _productService.GetProductUrlKey(p));
        }

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
            is_preorder = o.IsPreorder,
            preorder_delivery_date = o.PreorderDeliveryDate,
            preorder_slot_label = o.PreorderSlotLabel,
            items = o.Items.Select(i => new
            {
                id = i.Id,
                product_id = i.ProductId,
                url_key = i.ProductId.HasValue && urlKeyByProductId.TryGetValue(i.ProductId.Value, out var uk) ? uk : null,
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

        var parentMap2 = await _db.Products
            .Where(p => productIds.Contains(p.Id) && p.ParentId != null)
            .Select(p => new { p.Id, p.ParentId })
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.ParentId!.Value);

        var imageIds2 = productIds
            .Select(id => parentMap2.TryGetValue(id, out var pid) ? pid : id)
            .Distinct()
            .ToList();

        var rawPaths2 = (await _db.ProductImages
            .Where(pi => imageIds2.Contains(pi.ProductId))
            .OrderBy(pi => pi.Position)
            .ToListAsync())
            .GroupBy(pi => pi.ProductId)
            .ToDictionary(g => g.Key, g => g.First().Path);

        var imagesByProductId = new Dictionary<int, string>();
        foreach (var pId in productIds)
        {
            var lookupId = parentMap2.TryGetValue(pId, out var pid) ? pid : pId;
            if (rawPaths2.TryGetValue(lookupId, out var path))
                imagesByProductId[pId] = path;
        }

        var orderedProducts = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .Include(p => p.AttributeValues)
            .Include(p => p.Flats)
            .AsNoTracking()
            .ToListAsync();

        var urlKeyByProductId = orderedProducts
            .ToDictionary(p => p.Id, p => _productService.GetProductUrlKey(p));

        // Resolved server-side so the app never has to re-derive the refund
        // window's business rule — see AccountService.RequestRefundAsync for
        // the matching enforcement.
        var deliveredAt = AccountService.ResolveDeliveredAt(order);
        var refundWindowDays = await _accountService.GetRefundWindowDaysAsync();

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
            extra_charges_total = order.ExtraChargesTotal ?? 0,
            formatted_extra_charges_total = Fmt(order.ExtraChargesTotal),
            extra_charges = order.ExtraCharges
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    charge_type = c.ChargeType,
                    rate = c.Rate,
                    amount = c.Amount,
                    formatted_amount = Fmt(c.Amount)
                })
                .ToList(),
            created_at = order.CreatedAt,
            updated_at = order.UpdatedAt,
            delivered_at = deliveredAt,
            refund_window_days = refundWindowDays,
            is_preorder = order.IsPreorder,
            preorder_delivery_date = order.PreorderDeliveryDate,
            preorder_slot_label = order.PreorderSlotLabel,
            items = order.Items.Select(i =>
            {
                var imagePath = i.ProductId.HasValue && imagesByProductId.TryGetValue(i.ProductId.Value, out var p) ? p : null;
                var imageUrl = ResolveSmallImageUrl(imagePath);
                var urlKey = i.ProductId.HasValue && urlKeyByProductId.TryGetValue(i.ProductId.Value, out var uk) ? uk : null;
                return new
                {
                    id = i.Id,
                    sku = i.Sku,
                    type = i.Type,
                    name = i.Name,
                    product_id = i.ProductId,
                    url_key = urlKey,
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

    /// <summary>Download order invoice as PDF (cached for 10 minutes)</summary>
    [HttpGet("{id:int}/invoice")]
    public async Task<IActionResult> DownloadInvoice(int id)
    {
        Console.WriteLine($"[Invoice] Download request for order {id}");

        if (!int.TryParse(User.FindFirst("customer_id")?.Value, out var customerId) || customerId == 0)
        {
            Console.WriteLine("[Invoice] Unauthorized - no customer_id");
            return Unauthorized();
        }

        Console.WriteLine($"[Invoice] Customer {customerId} requesting invoice for order {id}");

        try
        {
            var pdfBytes = await _invoiceService.GenerateInvoicePdfAsync(id, customerId);
            Console.WriteLine($"[Invoice] Generated PDF: {pdfBytes?.Length ?? 0} bytes");

            if (pdfBytes == null || pdfBytes.Length == 0)
                return BadRequest(new { message = "Failed to generate invoice PDF - empty result." });

            return File(pdfBytes, "application/pdf", $"invoice_order_{id}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[Invoice] Error: {ex.Message}");
            return NotFound(new { message = $"Invoice generation failed: {ex.Message}" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Invoice] Exception: {ex}");
            return StatusCode(500, new { message = "Error generating invoice", error = ex.Message, type = ex.GetType().Name });
        }
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
