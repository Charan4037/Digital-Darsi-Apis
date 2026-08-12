using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DOSApi.Controllers.Shop;
using DOSApi.Data;
using DOSApi.Models;
using DOSApi.Models.Customer;
using DOSApi.Models.Sales;

namespace DOSApi.Services;

public class CheckoutService
{
    private readonly DOSDbContext _db;
    private readonly ILogger<CheckoutService> _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceAreaService _serviceArea;
    private readonly ExtraChargeService _extraCharge;
    private readonly DeliveryChargeService _deliveryCharge;

    public CheckoutService(DOSDbContext db, ILogger<CheckoutService> log, IServiceScopeFactory scopeFactory, ServiceAreaService serviceArea, ExtraChargeService extraCharge, DeliveryChargeService deliveryCharge)
    {
        _db = db;
        _log = log;
        _scopeFactory = scopeFactory;
        _serviceArea = serviceArea;
        _extraCharge = extraCharge;
        _deliveryCharge = deliveryCharge;
    }

    public async Task<(bool success, string message, int? addressId)> SaveCheckoutAddressAsync(
        int cartId, string firstName, string lastName, string address, string city,
        string state, string country, string postcode, string phone, string? email,
        bool useForShipping, bool defaultAddress, int? customerId)
    {
        // Service-area guard — reject before persisting anything for a
        // pincode outside the allowlist (see ServiceAreaService).
        if (!await _serviceArea.IsPincodeServiceableAsync(postcode))
            return (false, await _serviceArea.BuildUnserviceableMessageAsync(), null);

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

    /// <summary>Loads a cart's items with the Product/Category includes
    /// DeliveryChargeService (and ExtraChargeService) need to resolve
    /// category-scoped pricing — same include shape PlaceOrderAsync uses.</summary>
    private async Task<List<Models.Cart.CartItem>> LoadCartItemsForPricingAsync(int cartId)
    {
        return await _db.CartItems
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Product).ThenInclude(p => p!.Categories)
            .Include(i => i.Product).ThenInclude(p => p!.Parent).ThenInclude(p => p!.Categories)
            .Where(i => i.CartId == cartId)
            .ToListAsync();
    }

    /// <summary>Resolves the active delivery tiers' prices — category-aware
    /// (highest applicable rate wins) when a cart is given, otherwise each
    /// tier's global default. See DeliveryChargeService.</summary>
    public async Task<List<ShippingRateDto>> GetShippingRatesAsync(int? cartId = null)
    {
        var items = cartId.HasValue
            ? await LoadCartItemsForPricingAsync(cartId.Value)
            : new List<Models.Cart.CartItem>();

        return await _deliveryCharge.ResolveRatesAsync(items);
    }

    // Sync wrapper retained for call sites (GraphQL resolver) that can't easily
    // become async. Blocks briefly on a small DB read — acceptable here.
    public List<ShippingRateDto> GetShippingRates(int? cartId = null)
        => GetShippingRatesAsync(cartId).GetAwaiter().GetResult();

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
        string? razorpaySignature = null,
        string? guestSessionToken = null)
    {
        var cart = await _db.Carts
            .AsSplitQuery()
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Categories)
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent).ThenInclude(p => p!.Categories)
            .Include(c => c.Payment)
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.Id == cartId && c.IsActive == true);

        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", null, null);

        // Service-area guard — re-checked here (not just at address-save
        // time) as the authoritative gate, in case the allowlist changed
        // between the two steps or this cart's address predates it.
        var shippingOrBillingAddress = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing");
        if (!await _serviceArea.IsPincodeServiceableAsync(shippingOrBillingAddress?.Postcode))
            return (false, await _serviceArea.BuildUnserviceableMessageAsync(), null, null);

        // Minimum order value guard — reads from core_config so ops can
        // change the threshold without a code deploy. Deliberately based on
        // merchandise subtotal only (not extra charges or shipping) — same
        // basis the cart screen's own eligibility check uses, so a cart that
        // clears the bar there can't turn around and get rejected here.
        var minRow = await _db.CoreConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == ShopCheckoutSettingsController.MinOrderKey);
        if (minRow?.Value != null &&
            decimal.TryParse(minRow.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var minOrder) &&
            minOrder > 0)
        {
            var cartTotal = cart.SubTotal ?? 0m;
            if (cartTotal < minOrder)
                return (false, $"Minimum order value is ₹{minOrder:0}. Please add ₹{(minOrder - cartTotal):0} more to proceed.", null, null);
        }

        // Generate increment ID
        var lastOrder = await _db.Orders.OrderByDescending(o => o.Id).FirstOrDefaultAsync();
        var incrementId = lastOrder != null
            ? (int.Parse(lastOrder.IncrementId ?? "100000") + 1).ToString()
            : "100001";

        // Resolve the selected delivery tier's price the same way it was
        // shown to the customer at checkout — recomputed here (not trusted
        // from an earlier response) so category-scoped overrides that
        // changed since, or the cart's actual category mix, are always
        // authoritative at the moment the order is placed.
        ShippingRateDto? selectedDelivery = null;
        if (!string.IsNullOrEmpty(cart.ShippingMethod))
        {
            var resolvedRates = await _deliveryCharge.ResolveRatesAsync(cart.Items);
            selectedDelivery = resolvedRates.FirstOrDefault(r => r.Method == cart.ShippingMethod || r.Code == cart.ShippingMethod);
        }

        var shippingAmount = selectedDelivery?.Price ?? 0m;
        var shippingTitle = selectedDelivery?.MethodTitle
            ?? (cart.ShippingMethod?.Contains("free") == true ? "Free Shipping" : "Flat Rate");
        var shippingDescription = selectedDelivery?.Description;

        // Same admin-defined charges (Handling, Processing Fee, etc.) shown
        // on the cart/checkout review — recomputed here as the authoritative
        // amount actually charged, in case the allowlist changed since. The
        // itemized lines are persisted below (order_extra_charges) so the
        // invoice can show the same breakdown the customer saw at checkout.
        var (extraChargeLines, extraChargesTotal) = await _extraCharge.ComputeAsync(cart.Items);

        // Cart.CustomerFirstName/LastName are never populated anywhere in the checkout
        // flow — the shipping (falling back to billing) address is the only reliable
        // source for the customer's name at this point, so use it if the cart fields
        // are blank (they always are today).
        var cartAddresses = await _db.Addresses.Where(a => a.CartId == cartId).ToListAsync();
        var nameSourceAddress = cartAddresses.FirstOrDefault(a => a.AddressType == "cart_shipping")
            ?? cartAddresses.FirstOrDefault(a => a.AddressType == "cart_billing")
            ?? cartAddresses.FirstOrDefault();

        var order = new Order
        {
            IncrementId = incrementId,
            Status = "pending",
            ChannelName = "Default",
            IsGuest = cart.IsGuest,
            CustomerEmail = cart.CustomerEmail,
            CustomerFirstName = !string.IsNullOrWhiteSpace(cart.CustomerFirstName) ? cart.CustomerFirstName : nameSourceAddress?.FirstName,
            CustomerLastName = !string.IsNullOrWhiteSpace(cart.CustomerLastName) ? cart.CustomerLastName : nameSourceAddress?.LastName,
            ShippingMethod = cart.ShippingMethod,
            ShippingTitle = shippingTitle,
            ShippingDescription = shippingDescription,
            CouponCode = cart.CouponCode,
            TotalItemCount = cart.ItemsCount,
            TotalQtyOrdered = (int)(cart.ItemsQty ?? 0),
            BaseCurrencyCode = cart.BaseCurrencyCode ?? "INR",
            ChannelCurrencyCode = cart.ChannelCurrencyCode ?? "INR",
            OrderCurrencyCode = cart.CartCurrencyCode ?? "INR",
            GrandTotal = (cart.GrandTotal ?? 0m) + shippingAmount + extraChargesTotal,
            BaseGrandTotal = (cart.BaseGrandTotal ?? 0m) + shippingAmount + extraChargesTotal,
            ExtraChargesTotal = extraChargesTotal,
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

        // Snapshot the itemized extra-charge breakdown against this order —
        // orders.extra_charges_total (set above) is just their sum.
        for (int i = 0; i < extraChargeLines.Count; i++)
        {
            var line = extraChargeLines[i];
            _db.OrderExtraCharges.Add(new OrderExtraCharge
            {
                OrderId = order.Id,
                Name = line.Name,
                ChargeType = line.ChargeType,
                Rate = line.Rate,
                Amount = line.Amount,
                SortOrder = i,
                CreatedAt = DateTime.UtcNow
            });
        }

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

        // Copy addresses (cartAddresses fetched above, before order creation, to derive the name)
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

        // Deactivate cart and remove all its items so any subsequent cart
        // fetch returns an empty cart rather than stale ordered items.
        cart.IsActive = false;
        cart.UpdatedAt = DateTime.UtcNow;
        cart.ItemsCount = 0;
        cart.ItemsQty = 0;
        _db.CartItems.RemoveRange(cart.Items);

        // Deduct inventory — one batched query instead of one round trip per
        // cart item (was N+1; each extra item used to cost its own full
        // remote round trip).
        var productIds = cart.Items.Select(ci => ci.ProductId).ToList();
        var inventories = (await _db.ProductInventories
            .Where(i => productIds.Contains(i.ProductId))
            .ToListAsync())
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var ci in cart.Items)
        {
            if (inventories.TryGetValue(ci.ProductId, out var inv))
            {
                inv.Qty = Math.Max(0, inv.Qty - ci.Quantity);
            }
        }

        await _db.SaveChangesAsync();

        // Fire-and-forget the order-placed push. Notification failures must
        // not roll back the order — the order is the source of truth. A
        // request-scoped NotificationService can't be used here: its
        // DbContext is disposed the moment the response is sent, so the
        // send resolves a fresh instance from its own DI scope on a
        // detached task instead of blocking the client on FCM latency
        // (previously caused client-side timeouts on orders that had, in
        // fact, already succeeded).
        var orderId = order.Id;
        var orderIncrementId = order.IncrementId;
        var orderCustomerId = order.CustomerId;
        var orderGrandTotal = order.GrandTotal;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var pushOrder = new Order
                {
                    Id = orderId,
                    IncrementId = orderIncrementId,
                    CustomerId = orderCustomerId,
                    GrandTotal = orderGrandTotal,
                };
                if (orderCustomerId.HasValue && orderCustomerId.Value > 0)
                    await notify.SendOrderPlacedAsync(pushOrder);
                else if (!string.IsNullOrWhiteSpace(guestSessionToken))
                    await notify.SendGuestOrderPlacedAsync(pushOrder, guestSessionToken);

                await notify.SendOrderPlacedToAdminsAsync(pushOrder);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[Checkout] Order placed but push failed (orderId={OrderId})", orderId);
            }
        });

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
