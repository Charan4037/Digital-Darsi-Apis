using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;
using BagistoApi.Models.Customer;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/checkout-orders")]
[Tags("CheckoutOrder")]
[Authorize]
public class ShopCheckoutOrderController : ControllerBase
{
    private readonly CheckoutService _checkoutService;
    private readonly CartService _cartService;
    private readonly AuthService _authService;
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopCheckoutOrderController(
        CheckoutService checkoutService,
        CartService cartService,
        AuthService authService,
        BagistoDbContext db,
        IConfiguration config)
    {
        _checkoutService = checkoutService;
        _cartService = cartService;
        _authService = authService;
        _db = db;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
    }

    private static string Fmt(decimal? v) => $"${(v ?? 0):N2}";

    /// <summary>Get checkout summary / current cart state</summary>
    [HttpGet]
    public async Task<IActionResult> GetCheckoutSummary(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var addresses = await _db.Addresses.Where(a => a.CartId == cart.Id).ToListAsync();
        var billing = addresses.FirstOrDefault(a => a.AddressType == "cart_billing");
        var shipping = addresses.FirstOrDefault(a => a.AddressType == "cart_shipping");

        var rates = _checkoutService.GetShippingRates();
        var paymentMethods = _checkoutService.GetPaymentMethods();

        var summary = new
        {
            id = cart.Id,
            customer_email = cart.CustomerEmail,
            customer_first_name = cart.CustomerFirstName,
            customer_last_name = cart.CustomerLastName,
            is_guest = (cart.IsGuest ?? false) ? 1 : 0,
            is_active = (cart.IsActive ?? false) ? 1 : 0,
            items_count = cart.ItemsCount,
            items_qty = cart.ItemsQty,
            shipping_method = cart.ShippingMethod,
            coupon_code = cart.CouponCode,
            is_gift = cart.IsGift ? 1 : 0,
            base_currency_code = cart.BaseCurrencyCode,
            channel_currency_code = cart.ChannelCurrencyCode,
            cart_currency_code = cart.CartCurrencyCode,
            global_currency_code = cart.GlobalCurrencyCode,
            exchange_rate = cart.ExchangeRate,
            sub_total = cart.SubTotal ?? 0,
            formatted_sub_total = Fmt(cart.SubTotal),
            base_sub_total = cart.BaseSubTotal ?? 0,
            formatted_base_sub_total = Fmt(cart.BaseSubTotal),
            tax_total = cart.TaxTotal ?? 0,
            formatted_tax_total = Fmt(cart.TaxTotal),
            base_tax_total = cart.BaseTaxTotal ?? 0,
            discount_amount = cart.DiscountAmount ?? 0,
            formatted_discount_amount = Fmt(cart.DiscountAmount),
            base_discount_amount = cart.BaseDiscountAmount ?? 0,
            grand_total = cart.GrandTotal ?? 0,
            formatted_grand_total = Fmt(cart.GrandTotal),
            base_grand_total = cart.BaseGrandTotal ?? 0,
            formatted_base_grand_total = Fmt(cart.BaseGrandTotal),
            applied_cart_rule_ids = cart.AppliedCartRuleIds,
            items = cart.Items.Select(i => new
            {
                id = i.Id,
                product_id = i.ProductId,
                sku = i.Sku,
                type = i.Type,
                name = i.Name,
                quantity = i.Quantity,
                price = i.Price,
                formatted_price = Fmt(i.Price),
                base_price = i.BasePrice,
                formatted_base_price = Fmt(i.BasePrice),
                total = i.Total,
                formatted_total = Fmt(i.Total),
                base_total = i.BaseTotal,
                formatted_base_total = Fmt(i.BaseTotal),
                tax_percent = i.TaxPercent,
                tax_amount = i.TaxAmount ?? 0,
                base_tax_amount = i.BaseTaxAmount ?? 0,
                discount_percent = i.DiscountPercent,
                discount_amount = i.DiscountAmount,
                base_discount_amount = i.BaseDiscountAmount,
                weight = i.Weight,
                total_weight = i.TotalWeight,
                additional = i.Additional,
                created_at = i.CreatedAt,
                updated_at = i.UpdatedAt
            }),
            billing_address = billing != null ? FormatAddress(billing) : null,
            shipping_address = shipping != null ? FormatAddress(shipping) : null,
            payment_methods = paymentMethods.Select(p => new
            {
                id = p.Id,
                method = p.Method,
                method_title = p.Title,
                description = p.Description,
                is_allowed = p.IsAllowed ? 1 : 0
            }),
            shipping_methods = rates
                .GroupBy(r => r.Carrier)
                .Select(g => new
                {
                    carrier_code = g.Key,
                    carrier_title = g.First().CarrierTitle,
                    rates = g.Select(r => new
                    {
                        id = r.Id,
                        carrier = r.Carrier,
                        carrier_title = r.CarrierTitle,
                        method = r.Method,
                        method_title = r.MethodTitle,
                        method_description = r.Description,
                        price = r.Price,
                        formatted_price = r.FormattedPrice,
                        base_price = r.BasePrice,
                        formatted_base_price = r.BaseFormattedPrice,
                        discount_amount = 0,
                        base_discount_amount = 0
                    })
                }),
            created_at = cart.CreatedAt,
            updated_at = cart.UpdatedAt
        };

        return Ok(summary);
    }

    /// <summary>Get a placed order by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCheckoutOrder(int id)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
            return NotFound(new { message = "Order not found." });

        var addresses = await _db.Addresses
            .Where(a => a.OrderId == order.Id)
            .ToListAsync();

        var billing = addresses.FirstOrDefault(a => a.AddressType == "order_billing");
        var shipping = addresses.FirstOrDefault(a => a.AddressType == "order_shipping");

        return Ok(new
        {
            id = order.Id,
            increment_id = order.IncrementId,
            status = order.Status,
            channel_name = order.ChannelName,
            is_guest = (order.IsGuest ?? false) ? 1 : 0,
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
            discount_amount = order.DiscountAmount ?? 0,
            formatted_discount_amount = Fmt(order.DiscountAmount),
            base_discount_amount = order.BaseDiscountAmount ?? 0,
            shipping_amount = order.ShippingAmount ?? 0,
            formatted_shipping_amount = Fmt(order.ShippingAmount),
            base_shipping_amount = order.BaseShippingAmount ?? 0,
            applied_cart_rule_ids = order.AppliedCartRuleIds,
            items = order.Items.Select(i => new
            {
                id = i.Id,
                sku = i.Sku,
                type = i.Type,
                name = i.Name,
                product_id = i.ProductId,
                product_type = i.ProductType,
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
            }),
            billing_address = billing != null ? FormatAddress(billing) : null,
            shipping_address = shipping != null ? FormatAddress(shipping) : null,
            payment = order.Payment != null ? new
            {
                id = order.Payment.Id,
                method = order.Payment.Method,
                method_title = order.Payment.MethodTitle,
                additional = order.Payment.Additional
            } : null,
            created_at = order.CreatedAt,
            updated_at = order.UpdatedAt
        });
    }

    /// <summary>Place order from current cart</summary>
    [HttpPost]
    public async Task<IActionResult> PlaceOrder(
        [FromHeader(Name = "X-Cart-Token")] string? cartToken)
    {
        var customerId = _authService.GetCurrentCustomerId() ?? 0;
        var cart = await _cartService.GetCartAsync(customerId > 0 ? customerId : null, cartToken);
        if (cart == null) return NotFound(new { message = "Cart not found." });

        var effectiveCustomerId = customerId > 0 ? customerId : (int?)null;
        var guestSession = effectiveCustomerId == null ? cartToken : null;
        var (success, message, orderId, incrementId) = await _checkoutService.PlaceOrderAsync(
            cart.Id, effectiveCustomerId, guestSessionToken: guestSession);

        if (!success)
            return BadRequest(new { message });

        // Fetch the placed order and return in full Bagisto format
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
            return Ok(new { message, order_id = orderId, increment_id = incrementId });

        var addresses = await _db.Addresses
            .Where(a => a.OrderId == order.Id)
            .ToListAsync();

        var billing = addresses.FirstOrDefault(a => a.AddressType == "order_billing");
        var shipping = addresses.FirstOrDefault(a => a.AddressType == "order_shipping");

        return Ok(new
        {
            data = new
            {
                id = order.Id,
                increment_id = order.IncrementId,
                status = order.Status,
                channel_name = order.ChannelName,
                is_guest = (order.IsGuest ?? false) ? 1 : 0,
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
                discount_amount = order.DiscountAmount ?? 0,
                formatted_discount_amount = Fmt(order.DiscountAmount),
                base_discount_amount = order.BaseDiscountAmount ?? 0,
                shipping_amount = order.ShippingAmount ?? 0,
                formatted_shipping_amount = Fmt(order.ShippingAmount),
                base_shipping_amount = order.BaseShippingAmount ?? 0,
                applied_cart_rule_ids = order.AppliedCartRuleIds,
                items = order.Items.Select(i => new
                {
                    id = i.Id,
                    sku = i.Sku,
                    type = i.Type,
                    name = i.Name,
                    product_id = i.ProductId,
                    product_type = i.ProductType,
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
                }),
                billing_address = billing != null ? FormatAddress(billing) : null,
                shipping_address = shipping != null ? FormatAddress(shipping) : null,
                payment = order.Payment != null ? new
                {
                    id = order.Payment.Id,
                    method = order.Payment.Method,
                    method_title = order.Payment.MethodTitle,
                    additional = order.Payment.Additional
                } : null,
                created_at = order.CreatedAt,
                updated_at = order.UpdatedAt
            },
            message
        });
    }

    private static object FormatAddress(Address a) => new
    {
        id = a.Id,
        address_type = a.AddressType,
        parent_address_id = a.ParentAddressId,
        customer_id = a.CustomerId,
        cart_id = a.CartId,
        order_id = a.OrderId,
        first_name = a.FirstName,
        last_name = a.LastName,
        gender = a.Gender,
        company_name = a.CompanyName,
        address = (a.AddressLine ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries),
        city = a.City,
        state = a.State,
        country = a.Country,
        postcode = a.Postcode,
        email = a.Email,
        phone = a.Phone,
        vat_id = a.VatId,
        default_address = a.DefaultAddress ? 1 : 0,
        use_for_shipping = a.UseForShipping ? 1 : 0,
        additional = a.Additional
    };
}
