using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models;
using BagistoApi.Models.Customer;
using BagistoApi.Models.Sales;

namespace BagistoApi.Services;

public class CheckoutService
{
    private readonly BagistoDbContext _db;
    private readonly NotificationService _notify;
    private readonly ILogger<CheckoutService> _log;

    public CheckoutService(BagistoDbContext db, NotificationService notify, ILogger<CheckoutService> log)
    {
        _db = db;
        _notify = notify;
        _log = log;
    }

    public async Task<(bool success, string message, int? addressId)> SaveCheckoutAddressAsync(
        int cartId, string firstName, string lastName, string address, string city,
        string state, string country, string postcode, string phone, string? email,
        bool useForShipping, bool defaultAddress, int? customerId)
    {
        // Save billing address
        var billing = new Address
        {
            AddressType = "cart_billing",
            CartId = cartId,
            CustomerId = customerId,
            FirstName = firstName,
            LastName = lastName,
            AddressLine = address,
            City = city,
            State = state,
            Country = country,
            Postcode = postcode,
            Phone = phone,
            Email = email,
            UseForShipping = useForShipping,
            DefaultAddress = defaultAddress,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Addresses.Add(billing);

        if (useForShipping)
        {
            var shipping = new Address
            {
                AddressType = "cart_shipping",
                CartId = cartId,
                CustomerId = customerId,
                FirstName = firstName,
                LastName = lastName,
                AddressLine = address,
                City = city,
                State = state,
                Country = country,
                Postcode = postcode,
                Phone = phone,
                Email = email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Addresses.Add(shipping);
        }

        await _db.SaveChangesAsync();
        return (true, "Address saved successfully.", billing.Id);
    }

    public async Task<List<ShippingRateDto>> GetShippingRatesAsync()
    {
        var rows = await _db.DeliveryTypes
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Id)
            .ToListAsync();

        return rows.Select(d => new ShippingRateDto
        {
            Id = d.Id,
            Code = $"{d.Code}_{d.Code}",
            Label = d.Name,
            Description = d.Description,
            Method = $"{d.Code}_{d.Code}",
            MethodTitle = d.Name,
            Price = d.Price,
            FormattedPrice = $"₹{d.Price:0.00}",
            BasePrice = d.Price,
            BaseFormattedPrice = $"₹{d.Price:0.00}",
            Carrier = d.Code,
            CarrierTitle = d.Name
        }).ToList();
    }

    // Sync wrapper retained for call sites (GraphQL resolver) that can't easily
    // become async. Blocks briefly on a small DB read — acceptable here.
    public List<ShippingRateDto> GetShippingRates()
        => GetShippingRatesAsync().GetAwaiter().GetResult();

    public List<PaymentMethodDto> GetPaymentMethods()
    {
        return new List<PaymentMethodDto>
        {
            new() { Id = 1, Method = "cashondelivery", Title = "Cash On Delivery", Description = "Pay when you receive", IsAllowed = true },
            new() { Id = 2, Method = "moneytransfer", Title = "Money Transfer", Description = "Bank transfer", IsAllowed = true }
        };
    }

    public async Task<(bool success, string message)> SaveShippingMethodAsync(int cartId, string shippingMethod)
    {
        var cart = await _db.Carts.FindAsync(cartId);
        if (cart == null) return (false, "Cart not found.");

        cart.ShippingMethod = shippingMethod;
        cart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (true, "Shipping method saved successfully.");
    }

    public async Task<(bool success, string message, string? paymentGatewayUrl, string? paymentData)> SavePaymentMethodAsync(int cartId, string paymentMethod)
    {
        var cart = await _db.Carts.FindAsync(cartId);
        if (cart == null) return (false, "Cart not found.", null, null);

        var existing = await _db.CartPayments.FirstOrDefaultAsync(p => p.CartId == cartId);
        if (existing != null)
        {
            existing.Method = paymentMethod;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.CartPayments.Add(new Models.Cart.CartPayment
            {
                CartId = cartId,
                Method = paymentMethod,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        return (true, "Payment method saved successfully.", null, null);
    }

    public async Task<(bool success, string message, int? orderId, string? orderIncrementId)> PlaceOrderAsync(
        int cartId,
        int? customerId,
        string? razorpayPaymentId = null,
        string? razorpayOrderId = null,
        string? razorpaySignature = null)
    {
        var cart = await _db.Carts
            .Include(c => c.Items)
            .Include(c => c.Payment)
            .FirstOrDefaultAsync(c => c.Id == cartId && c.IsActive == true);

        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", null, null);

        // Generate increment ID
        var lastOrder = await _db.Orders.OrderByDescending(o => o.Id).FirstOrDefaultAsync();
        var incrementId = lastOrder != null
            ? (int.Parse(lastOrder.IncrementId ?? "100000") + 1).ToString()
            : "100001";

        // Resolve the selected delivery type (if any) so the order carries the
        // correct shipping title/price rather than a hardcoded placeholder.
        DeliveryType? selectedDelivery = null;
        if (!string.IsNullOrEmpty(cart.ShippingMethod))
        {
            var raw = cart.ShippingMethod;
            var idx = raw.IndexOf('_');
            var code = idx > 0 ? raw.Substring(0, idx) : raw;
            selectedDelivery = await _db.DeliveryTypes
                .FirstOrDefaultAsync(d => d.Code == code && d.IsActive);
        }

        var shippingAmount = selectedDelivery?.Price ?? 0m;
        var shippingTitle = selectedDelivery?.Name
            ?? (cart.ShippingMethod?.Contains("free") == true ? "Free Shipping" : "Flat Rate");
        var shippingDescription = selectedDelivery?.Description;

        var order = new Order
        {
            IncrementId = incrementId,
            Status = "pending",
            ChannelName = "Default",
            IsGuest = cart.IsGuest,
            CustomerEmail = cart.CustomerEmail,
            CustomerFirstName = cart.CustomerFirstName,
            CustomerLastName = cart.CustomerLastName,
            ShippingMethod = cart.ShippingMethod,
            ShippingTitle = shippingTitle,
            ShippingDescription = shippingDescription,
            CouponCode = cart.CouponCode,
            TotalItemCount = cart.ItemsCount,
            TotalQtyOrdered = (int)(cart.ItemsQty ?? 0),
            BaseCurrencyCode = cart.BaseCurrencyCode ?? "INR",
            ChannelCurrencyCode = cart.ChannelCurrencyCode ?? "INR",
            OrderCurrencyCode = cart.CartCurrencyCode ?? "INR",
            GrandTotal = (cart.GrandTotal ?? 0m) + shippingAmount,
            BaseGrandTotal = (cart.BaseGrandTotal ?? 0m) + shippingAmount,
            SubTotal = cart.SubTotal,
            BaseSubTotal = cart.BaseSubTotal,
            TaxAmount = cart.TaxTotal,
            BaseTaxAmount = cart.BaseTaxTotal,
            DiscountAmount = cart.DiscountAmount,
            BaseDiscountAmount = cart.BaseDiscountAmount,
            ShippingAmount = shippingAmount,
            BaseShippingAmount = shippingAmount,
            CustomerId = customerId ?? cart.CustomerId,
            ChannelId = cart.ChannelId,
            CartId = cart.Id,
            AppliedCartRuleIds = cart.AppliedCartRuleIds,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Create order items
        foreach (var ci in cart.Items)
        {
            var orderItem = new OrderItem
            {
                OrderId = order.Id,
                Sku = ci.Sku,
                Type = ci.Type,
                Name = ci.Name,
                QtyOrdered = ci.Quantity,
                Price = ci.Price,
                BasePrice = ci.BasePrice,
                Total = ci.Total,
                BaseTotal = ci.BaseTotal,
                TaxAmount = ci.TaxAmount,
                BaseTaxAmount = ci.BaseTaxAmount,
                DiscountAmount = ci.DiscountAmount,
                BaseDiscountAmount = ci.BaseDiscountAmount,
                Weight = ci.Weight,
                TotalWeight = ci.TotalWeight,
                ProductId = ci.ProductId,
                ProductType = ci.Type,
                Additional = ci.Additional,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.OrderItems.Add(orderItem);
        }

        // Create order payment
        if (cart.Payment != null)
        {
            string? additional = null;
            if (!string.IsNullOrEmpty(razorpayPaymentId))
            {
                additional = System.Text.Json.JsonSerializer.Serialize(new
                {
                    razorpay_payment_id = razorpayPaymentId,
                    razorpay_order_id = razorpayOrderId,
                    razorpay_signature = razorpaySignature
                });
            }

            _db.OrderPayments.Add(new OrderPayment
            {
                OrderId = order.Id,
                Method = cart.Payment.Method,
                MethodTitle = cart.Payment.MethodTitle ?? cart.Payment.Method,
                Additional = additional,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Copy addresses
        var cartAddresses = await _db.Addresses.Where(a => a.CartId == cartId).ToListAsync();
        foreach (var addr in cartAddresses)
        {
            _db.Addresses.Add(new Address
            {
                AddressType = addr.AddressType.Replace("cart_", "order_"),
                OrderId = order.Id,
                CustomerId = addr.CustomerId,
                FirstName = addr.FirstName,
                LastName = addr.LastName,
                AddressLine = addr.AddressLine,
                City = addr.City,
                State = addr.State,
                Country = addr.Country,
                Postcode = addr.Postcode,
                Phone = addr.Phone,
                Email = addr.Email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Deactivate cart
        cart.IsActive = false;
        cart.UpdatedAt = DateTime.UtcNow;

        // Deduct inventory
        foreach (var ci in cart.Items)
        {
            var inv = await _db.ProductInventories.FirstOrDefaultAsync(i => i.ProductId == ci.ProductId);
            if (inv != null)
            {
                inv.Qty = Math.Max(0, inv.Qty - ci.Quantity);
            }
        }

        await _db.SaveChangesAsync();

        // Fire-and-forget the order-placed push. Notification failures must
        // not roll back the order — the order is the source of truth.
        try
        {
            await _notify.SendOrderPlacedAsync(order);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Checkout] Order placed but push failed (orderId={OrderId})", order.Id);
        }

        return (true, "Order placed successfully.", order.Id, order.IncrementId);
    }
}

public class ShippingRateDto
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public string Method { get; set; } = "";
    public string MethodTitle { get; set; } = "";
    public decimal Price { get; set; }
    public string FormattedPrice { get; set; } = "";
    public decimal BasePrice { get; set; }
    public string BaseFormattedPrice { get; set; } = "";
    public string Carrier { get; set; } = "";
    public string CarrierTitle { get; set; } = "";
}

public class PaymentMethodDto
{
    public int Id { get; set; }
    public string Method { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public bool IsAllowed { get; set; }
}
