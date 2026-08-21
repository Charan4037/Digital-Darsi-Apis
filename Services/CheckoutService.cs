using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DOSApi.Controllers.Shop;
using DOSApi.Data;
using DOSApi.Helpers;
using DOSApi.Models;
using DOSApi.Models.Customer;
using DOSApi.Models.Sales;

namespace DOSApi.Services;

public class CheckoutService
{
    // Payment method codes used across the DB/GraphQL/REST/Flutter layers —
    // "moneytransfer" is the pre-existing code for the Razorpay/online method
    // (kept as-is rather than renamed, to avoid touching every call site).
    public const string CodMethod = "cashondelivery";
    public const string OnlineMethod = "moneytransfer";

    private readonly DOSDbContext _db;
    private readonly ILogger<CheckoutService> _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceAreaService _serviceArea;
    private readonly ExtraChargeService _extraCharge;
    private readonly DeliveryChargeService _deliveryCharge;
    private readonly PaymentSettingsService _paymentSettings;
    private readonly RazorpayService _razorpay;

    public CheckoutService(
        DOSDbContext db, ILogger<CheckoutService> log, IServiceScopeFactory scopeFactory,
        ServiceAreaService serviceArea, ExtraChargeService extraCharge, DeliveryChargeService deliveryCharge,
        PaymentSettingsService paymentSettings, RazorpayService razorpay)
    {
        _db = db;
        _log = log;
        _scopeFactory = scopeFactory;
        _serviceArea = serviceArea;
        _extraCharge = extraCharge;
        _deliveryCharge = deliveryCharge;
        _paymentSettings = paymentSettings;
        _razorpay = razorpay;
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

    /// <summary>Only offers the methods the admin has actually enabled (see
    /// PaymentSettingsService) — online payment additionally requires
    /// working Razorpay credentials to be listed at all.</summary>
    public async Task<List<PaymentMethodDto>> GetPaymentMethodsAsync()
    {
        var settings = await _paymentSettings.GetSettingsAsync();
        var methods = new List<PaymentMethodDto>();
        if (settings.CodEnabled)
            methods.Add(new() { Id = 1, Method = CodMethod, Title = "Cash On Delivery", Description = "Pay when you receive", IsAllowed = true });
        if (settings.OnlineUsable)
            methods.Add(new() { Id = 2, Method = OnlineMethod, Title = "Pay Online", Description = "Pay securely via Razorpay", IsAllowed = true });
        return methods;
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

    public class CartTotals
    {
        public required decimal ShippingAmount { get; init; }
        public required string ShippingTitle { get; init; }
        public string? ShippingDescription { get; init; }
        public required List<ExtraChargeLine> ExtraChargeLines { get; init; }
        public required decimal ExtraChargesTotal { get; init; }
        public required decimal GrandTotal { get; init; }
    }

    /// <summary>Recomputes a cart's true grand total server-side — shipping
    /// tier, extra charges, and cart subtotal — the same way PlaceOrderAsync
    /// does, so a Razorpay order can be created for the real amount rather
    /// than whatever the client claims the total is. Shared by
    /// ShopPaymentController's create-order endpoint and PlaceOrderAsync
    /// itself so the two can never disagree.</summary>
    public async Task<CartTotals> ComputeCartTotalsAsync(Models.Cart.Cart cart)
    {
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

        var (extraChargeLines, extraChargesTotal) = await _extraCharge.ComputeAsync(cart.Items);

        return new CartTotals
        {
            ShippingAmount = shippingAmount,
            ShippingTitle = shippingTitle,
            ShippingDescription = shippingDescription,
            ExtraChargeLines = extraChargeLines,
            ExtraChargesTotal = extraChargesTotal,
            GrandTotal = (cart.GrandTotal ?? 0m) + shippingAmount + extraChargesTotal,
        };
    }

    /// <summary>Loads an active cart with the includes both
    /// ComputeCartTotalsAsync and order creation need.</summary>
    private async Task<Models.Cart.Cart?> LoadActiveCartAsync(int cartId)
    {
        return await _db.Carts
            .AsSplitQuery()
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Categories)
            .Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Parent).ThenInclude(p => p!.Categories)
            .Include(c => c.Payment)
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.Id == cartId && c.IsActive == true);
    }

    public class RazorpayOrderCreationResult
    {
        public required string OrderId { get; init; }
        public required long AmountPaise { get; init; }
        public required string Currency { get; init; }
        public required string KeyId { get; init; }
    }

    /// <summary>Creates a real Razorpay order for the cart's own recomputed
    /// total (never the client's claimed amount) and pins it onto the cart's
    /// CartPayment row so PlaceOrderAsync/the webhook can cross-check it
    /// later. Fails if online payment isn't enabled/configured.</summary>
    public async Task<(bool ok, string message, RazorpayOrderCreationResult? result)> CreateRazorpayOrderAsync(int cartId)
    {
        var settings = await _paymentSettings.GetSettingsAsync();
        if (!settings.OnlineUsable)
            return (false, "Online payment is currently unavailable.", null);

        var cart = await LoadActiveCartAsync(cartId);
        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", null);

        var totals = await ComputeCartTotalsAsync(cart);
        RazorpayService.RazorpayOrderResult order;
        try
        {
            order = await _razorpay.CreateOrderAsync(settings.RazorpayKeyId!, settings.RazorpayKeySecret!, totals.GrandTotal, "INR", $"cart-{cartId}");
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "[Checkout] Razorpay create-order failed for cart {CartId}", cartId);
            return (false, "Could not start online payment. Please try again.", null);
        }

        var payment = await _db.CartPayments.FirstOrDefaultAsync(p => p.CartId == cartId);
        if (payment == null)
        {
            payment = new Models.Cart.CartPayment { CartId = cartId, Method = OnlineMethod, CreatedAt = DateTime.UtcNow };
            _db.CartPayments.Add(payment);
        }
        payment.RazorpayOrderId = order.OrderId;
        payment.RazorpayAmount = totals.GrandTotal;
        payment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (true, "", new RazorpayOrderCreationResult
        {
            OrderId = order.OrderId,
            AmountPaise = order.AmountPaise,
            Currency = order.Currency,
            KeyId = settings.RazorpayKeyId!,
        });
    }

    /// <summary>Fast-fail convenience check for the UI — the real gate is
    /// still VerifyPaymentAsync inside PlaceOrderAsync, which re-verifies
    /// independently of whether this endpoint was ever called.</summary>
    public async Task<bool> VerifyRazorpayPaymentAsync(string razorpayOrderId, string razorpayPaymentId, string razorpaySignature)
    {
        var settings = await _paymentSettings.GetSettingsAsync();
        if (!settings.OnlineUsable) return false;

        var payment = await _db.CartPayments.AsNoTracking().FirstOrDefaultAsync(p => p.RazorpayOrderId == razorpayOrderId);
        if (payment == null) return false;

        return _razorpay.VerifyPaymentSignature(razorpayOrderId, razorpayPaymentId, razorpaySignature, settings.RazorpayKeySecret!);
    }

    public async Task<(bool success, string message, int? orderId, string? orderIncrementId)> PlaceOrderAsync(
        int cartId,
        int? customerId,
        string? razorpayPaymentId = null,
        string? razorpayOrderId = null,
        string? razorpaySignature = null,
        string? guestSessionToken = null)
    {
        var cart = await LoadActiveCartAsync(cartId);
        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", null, null);

        // Payment verification gate — the ONLY point that decides whether an
        // order gets created for an online payment. See VerifyPaymentAsync;
        // any failure here means no Order row is ever written.
        var paymentMethod = cart.Payment?.Method;
        var (paymentOk, paymentMessage, transactionId, isVerified) = await VerifyPaymentAsync(
            cart, paymentMethod, razorpayPaymentId, razorpayOrderId, razorpaySignature);
        if (!paymentOk)
            return (false, paymentMessage, null, null);

        return await CreateOrderFromCartAsync(cart, customerId, transactionId, isVerified, razorpaySignature, guestSessionToken);
    }

    /// <summary>Gates order creation on the admin's payment configuration and
    /// (for online payments) a real, server-verified Razorpay signature.
    /// Returns ok=false with no side effects if anything doesn't check out —
    /// callers must not create an Order when this fails.</summary>
    private async Task<(bool ok, string message, string? transactionId, bool isVerified)> VerifyPaymentAsync(
        Models.Cart.Cart cart, string? paymentMethod, string? razorpayPaymentId, string? razorpayOrderId, string? razorpaySignature)
    {
        var settings = await _paymentSettings.GetSettingsAsync();

        if (string.Equals(paymentMethod, CodMethod, StringComparison.OrdinalIgnoreCase))
        {
            if (!settings.CodEnabled)
                return (false, "Cash on Delivery is currently unavailable.", null, false);
            return (true, "", null, false);
        }

        if (string.Equals(paymentMethod, OnlineMethod, StringComparison.OrdinalIgnoreCase))
        {
            if (!settings.OnlineUsable)
                return (false, "Online payment is currently unavailable. Please choose Cash on Delivery.", null, false);

            if (string.IsNullOrWhiteSpace(razorpayPaymentId) || string.IsNullOrWhiteSpace(razorpayOrderId) || string.IsNullOrWhiteSpace(razorpaySignature))
                return (false, "Payment details missing. Please complete payment before placing the order.", null, false);

            // The order id must be the one we ourselves created for THIS
            // cart (see ShopPaymentController.CreateOrder) — otherwise a
            // valid signature from an unrelated payment could be replayed.
            if (!string.Equals(cart.Payment?.RazorpayOrderId, razorpayOrderId, StringComparison.Ordinal))
                return (false, "Payment order mismatch. Please retry payment.", null, false);

            if (!_razorpay.VerifyPaymentSignature(razorpayOrderId, razorpayPaymentId, razorpaySignature, settings.RazorpayKeySecret!))
                return (false, "Payment verification failed. Order was not placed.", null, false);

            return (true, "", razorpayPaymentId, true);
        }

        // Unknown/unset payment method — nothing to verify against.
        return (false, "Please select a valid payment method.", null, false);
    }

    /// <summary>Builds and saves the Order from an already cart — shared by
    /// the normal (client-verified) PlaceOrderAsync path and the Razorpay
    /// webhook's fallback path (HandleWebhookPaymentCapturedAsync), so both
    /// go through identical order-creation logic. [signature] is the raw
    /// razorpay_signature submitted by the client — null for the webhook
    /// path, which has no such field (its authenticity comes from the
    /// webhook's own X-Razorpay-Signature header, checked separately).</summary>
    private async Task<(bool success, string message, int? orderId, string? orderIncrementId)> CreateOrderFromCartAsync(
        Models.Cart.Cart cart, int? customerId, string? transactionId, bool isVerified, string? signature, string? guestSessionToken)
    {
        var cartId = cart.Id;

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
                return (false, $"Minimum order value is {PriceFormatter.Format(minOrder)}. Please add {PriceFormatter.Format(minOrder - cartTotal)} more to proceed.", null, null);
        }

        // Generate increment ID
        var lastOrder = await _db.Orders.OrderByDescending(o => o.Id).FirstOrDefaultAsync();
        var incrementId = lastOrder != null
            ? (int.Parse(lastOrder.IncrementId ?? "100000") + 1).ToString()
            : "100001";

        var totals = await ComputeCartTotalsAsync(cart);
        var shippingAmount = totals.ShippingAmount;
        var shippingTitle = totals.ShippingTitle;
        var shippingDescription = totals.ShippingDescription;
        var extraChargeLines = totals.ExtraChargeLines;
        var extraChargesTotal = totals.ExtraChargesTotal;

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

        // Create order payment — transactionId/isVerified come from
        // VerifyPaymentAsync (or the webhook's equivalent trusted call), not
        // from raw client-submitted strings.
        if (cart.Payment != null)
        {
            string? additional = null;
            if (!string.IsNullOrEmpty(transactionId))
            {
                additional = System.Text.Json.JsonSerializer.Serialize(new
                {
                    razorpay_payment_id = transactionId,
                    razorpay_order_id = cart.Payment.RazorpayOrderId,
                    razorpay_signature = signature,
                });
            }

            _db.OrderPayments.Add(new OrderPayment
            {
                OrderId = order.Id,
                Method = cart.Payment.Method,
                TransactionId = transactionId,
                IsVerified = isVerified,
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

    /// <summary>Safety-net path for ShopPaymentController's Razorpay webhook:
    /// a `payment.captured` event already proves the payment is real (the
    /// webhook signature check is the authentication for this call — no
    /// separate checkout-signature check needed here). Finishes placing the
    /// order for a cart whose client never completed its own verify+place
    /// call (e.g. app closed right after paying). No-ops if the cart was
    /// already converted to an order by the normal client path.</summary>
    public async Task<(bool success, string message, int? orderId, string? orderIncrementId)> HandleWebhookPaymentCapturedAsync(
        string razorpayOrderId, string razorpayPaymentId)
    {
        var cartPayment = await _db.CartPayments.FirstOrDefaultAsync(p => p.RazorpayOrderId == razorpayOrderId);
        if (cartPayment?.CartId == null)
            return (false, "No cart found for this Razorpay order.", null, null);

        var cart = await LoadActiveCartAsync(cartPayment.CartId.Value);
        if (cart == null || !cart.Items.Any())
            return (true, "Order already placed for this cart.", null, null);

        return await CreateOrderFromCartAsync(cart, cart.CustomerId, razorpayPaymentId, isVerified: true, signature: null, guestSessionToken: null);
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
    public int DeliveryHours { get; set; }
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
