using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly PreorderService _preorder;

    public CheckoutService(
        DOSDbContext db, ILogger<CheckoutService> log, IServiceScopeFactory scopeFactory,
        ServiceAreaService serviceArea, ExtraChargeService extraCharge, DeliveryChargeService deliveryCharge,
        PaymentSettingsService paymentSettings, RazorpayService razorpay, PreorderService preorder)
    {
        _db = db;
        _log = log;
        _scopeFactory = scopeFactory;
        _serviceArea = serviceArea;
        _extraCharge = extraCharge;
        _deliveryCharge = deliveryCharge;
        _paymentSettings = paymentSettings;
        _razorpay = razorpay;
        _preorder = preorder;
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

    /// <summary>Saves the customer's chosen delivery date + time slot for one
    /// distinct preorder group (rule) within a cart — a cart can have
    /// several groups at once (two preorder items under two different
    /// rules), each with its own independent selection, so this is scoped
    /// to [ruleId] rather than the whole cart. Validated against that
    /// group's own window/lead-time/slots; this is only a first-pass check
    /// for a responsive UI — PlacePreorderOrdersAsync re-validates
    /// authoritatively at the moment the order is actually placed, since
    /// the rule/slot can change between selection and placement.</summary>
    public async Task<(bool success, string message)> SavePreorderSelectionAsync(int cartId, int ruleId, DateTime deliveryDate, int slotId)
    {
        var cart = await _db.Carts
            .Include(c => c.Items)
            .Include(c => c.Addresses)
            .Include(c => c.PreorderSelections)
            .FirstOrDefaultAsync(c => c.Id == cartId);
        if (cart == null) return (false, "Cart not found.");

        var pincode = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;

        var resolved = await _preorder.ResolveForCartAsync(cart, pincode);
        var group = resolved.Groups.FirstOrDefault(g => g.Rule.Id == ruleId);
        if (group == null)
            return (false, "This delivery group is no longer part of your cart.");

        var windowDays = group.Rule.WindowDays ?? PreorderService.DefaultWindowDays;
        var minLeadHours = group.Rule.MinLeadHours ?? PreorderService.DefaultMinLeadHours;
        var today = PreorderService.TodayIst();
        var chosenDate = deliveryDate.Date;
        // Window now starts from today (day 0), not tomorrow — same-day
        // delivery is gated by the lead-time check below, not excluded outright.
        if (chosenDate < today || chosenDate > today.AddDays(windowDays - 1))
            return (false, $"Please choose a delivery date within the next {windowDays} day{(windowDays == 1 ? "" : "s")}.");

        var chosenSlot = group.Slots.FirstOrDefault(s => s.Id == slotId);
        if (chosenSlot == null)
            return (false, "Please choose a valid delivery time slot.");

        if (!PreorderService.IsSlotSelectable(chosenDate, chosenSlot, minLeadHours))
            return (false, "This time slot no longer allows enough lead time — please choose another.");

        var existing = cart.PreorderSelections.FirstOrDefault(s => s.PreorderRuleId == ruleId);
        if (existing != null)
        {
            existing.DeliveryDate = chosenDate;
            existing.SlotId = slotId;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.CartPreorderSelections.Add(new Models.Cart.CartPreorderSelection
            {
                CartId = cartId,
                PreorderRuleId = ruleId,
                DeliveryDate = chosenDate,
                SlotId = slotId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        cart.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return (true, "Delivery slot saved.");
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

    /// <summary>"4 PM" / "4:30 PM" — same 12-hour, no-leading-zero style the
    /// Flutter picker's own _formatHour uses, kept in sync manually since
    /// there's no shared formatting layer between the two apps.</summary>
    private static string FormatSlotTime(TimeSpan t)
    {
        var period = t.Hours >= 12 ? "PM" : "AM";
        var h12 = t.Hours == 0 ? 12 : (t.Hours > 12 ? t.Hours - 12 : t.Hours);
        return t.Minutes == 0 ? $"{h12} {period}" : $"{h12}:{t.Minutes:D2} {period}";
    }

    /// <summary>The order-facing snapshot of a chosen slot — "Evening · 4 PM - 6 PM"
    /// rather than just "Evening". Order.PreorderSlotLabel is a free-form
    /// string column, so baking the time range in here (once, at placement)
    /// means every UI that just prints it back (cart banner, checkout
    /// review, order detail, order history) shows the real delivery window
    /// with no further changes, and it stays accurate even if the rule's
    /// slot times are edited later. If the admin already labels a slot with
    /// its own time range (e.g. "6 AM - 8 AM"), it isn't duplicated.</summary>
    private static string FormatSlotLabel(Models.Catalog.PreorderSlot slot)
    {
        var range = $"{FormatSlotTime(slot.StartTime)} - {FormatSlotTime(slot.EndTime)}";
        var label = slot.Label?.Trim() ?? "";
        return string.IsNullOrEmpty(label) || string.Equals(label, range, StringComparison.OrdinalIgnoreCase)
            ? range
            : $"{label} · {range}";
    }

    /// <summary>Splits a cart's items into one bucket per distinct preorder
    /// group (rule) it resolves to — a cart can have several groups at once
    /// (two preorder items under two different rules), each of which
    /// becomes its own Order at placement time. Items belonging to no group
    /// at all are the "regular" portion and simply aren't returned here —
    /// callers derive that as whatever's left over.</summary>
    private static List<(PreorderService.CartPreorderGroup Group, List<Models.Cart.CartItem> Items)> GroupPreorderItems(
        List<Models.Cart.CartItem> items, PreorderService.CartPreorderResolution resolution)
    {
        var result = new List<(PreorderService.CartPreorderGroup, List<Models.Cart.CartItem>)>();
        foreach (var group in resolution.Groups)
        {
            var groupItems = items.Where(i => group.ProductIds.Contains(i.ProductId)).ToList();
            if (groupItems.Count > 0)
                result.Add((group, groupItems));
        }
        return result;
    }

    /// <summary>Same discount formula CartService.ApplyCouponAsync uses,
    /// parameterized on an arbitrary subtotal instead of cart.SubTotal — lets
    /// a split order's own subtotal drive its own discount, independently of
    /// the other resulting order.</summary>
    private async Task<decimal> ComputeCouponDiscountAsync(string? couponCode, decimal subtotal)
    {
        if (string.IsNullOrWhiteSpace(couponCode)) return 0m;

        var coupon = await _db.CartRuleCoupons
            .Include(c => c.CartRule)
            .FirstOrDefaultAsync(c => c.Code == couponCode);
        if (coupon?.CartRule == null || !coupon.CartRule.Status) return 0m;

        var rule = coupon.CartRule;
        return rule.ActionType switch
        {
            "cart_fixed" => rule.DiscountAmount,
            "cart_percent" or "percent_of_product_price_discount" => subtotal * rule.DiscountAmount / 100,
            _ => rule.DiscountAmount,
        };
    }

    private class GroupOrderTotals
    {
        public required decimal SubTotal { get; init; }
        public required decimal TaxAmount { get; init; }
        public required decimal DiscountAmount { get; init; }
        public required decimal ShippingAmount { get; init; }
        public required string ShippingTitle { get; init; }
        public string? ShippingDescription { get; init; }
        public required List<ExtraChargeLine> ExtraChargeLines { get; init; }
        public required decimal ExtraChargesTotal { get; init; }
        public required decimal GrandTotal { get; init; }
    }

    /// <summary>The per-order-group equivalent of ComputeCartTotalsAsync —
    /// scoped to a subset of a cart's items instead of the whole cart, so
    /// each order resulting from a mixed-cart split is priced as if it were
    /// its own fresh checkout against just its own items (subtotal/tax are
    /// exact sums of the already-snapshotted per-item fields; discount,
    /// shipping, and extra charges are all recomputed against just this
    /// subset — see CheckoutService.ComputeCouponDiscountAsync/
    /// DeliveryChargeService.ResolveRatesAsync/ExtraChargeService.ComputeAsync).
    ///
    /// [includeShipping] is false for preorder groups — a shipping *speed*
    /// tier ("Express — delivers within 2 hrs") is meaningless once delivery
    /// already happens at a specific slot the customer picked, so preorder
    /// orders never carry a shipping charge at all, regardless of whatever
    /// cart.ShippingMethod happens to hold.</summary>
    private async Task<GroupOrderTotals> ComputeGroupOrderTotalsAsync(Models.Cart.Cart cart, List<Models.Cart.CartItem> items, bool includeShipping = true)
    {
        var subtotal = items.Sum(i => i.Total);
        var taxAmount = items.Sum(i => i.TaxAmount ?? 0m);
        var discount = await ComputeCouponDiscountAsync(cart.CouponCode, subtotal);

        decimal shippingAmount = 0m;
        string shippingTitle = "Included with your delivery slot";
        string? shippingDescription = null;
        if (includeShipping && !string.IsNullOrEmpty(cart.ShippingMethod))
        {
            var resolvedRates = await _deliveryCharge.ResolveRatesAsync(items);
            var selectedDelivery = resolvedRates.FirstOrDefault(r => r.Method == cart.ShippingMethod || r.Code == cart.ShippingMethod);
            shippingAmount = selectedDelivery?.Price ?? 0m;
            shippingTitle = selectedDelivery?.MethodTitle
                ?? (cart.ShippingMethod?.Contains("free") == true ? "Free Shipping" : "Flat Rate");
            shippingDescription = selectedDelivery?.Description;
        }

        var (extraChargeLines, extraChargesTotal) = await _extraCharge.ComputeAsync(items);

        return new GroupOrderTotals
        {
            SubTotal = subtotal,
            TaxAmount = taxAmount,
            DiscountAmount = discount,
            ShippingAmount = shippingAmount,
            ShippingTitle = shippingTitle,
            ShippingDescription = shippingDescription,
            ExtraChargeLines = extraChargeLines,
            ExtraChargesTotal = extraChargesTotal,
            GrandTotal = subtotal + taxAmount - discount + shippingAmount + extraChargesTotal,
        };
    }

    public record PreorderGroupTotal(int RuleId, decimal SubTotal, decimal TaxAmount, decimal DiscountAmount, decimal ExtraChargesTotal, List<ExtraChargeLine> ExtraChargeLines, decimal GrandTotal);
    public record PreorderTotalsBreakdown(decimal GrandTotal, List<PreorderGroupTotal> Groups);

    /// <summary>Per-group AND combined authoritative totals for every
    /// preorder group currently pending in this cart — same figures
    /// CreateRazorpayOrderAsync's preorderOnly path actually charges (tax,
    /// coupon discount, and admin-defined extra charges all included, unlike
    /// a naive client-side sum of item prices). Exposed via
    /// GET /api/shop/preorder/total so the Review step can show the customer
    /// a full, exact breakdown before they tap Pay, instead of a
    /// client-computed "estimate" that can silently diverge from what
    /// Razorpay actually charges.</summary>
    public async Task<PreorderTotalsBreakdown> ComputePreorderTotalsBreakdownAsync(Models.Cart.Cart cart)
    {
        var pincode = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")?.Postcode
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing")?.Postcode;
        var preorderResolved = await _preorder.ResolveForCartAsync(cart, pincode);
        var groups = GroupPreorderItems(cart.Items, preorderResolved);

        var groupTotals = new List<PreorderGroupTotal>();
        decimal grand = 0m;
        foreach (var (group, items) in groups)
        {
            var totals = await ComputeGroupOrderTotalsAsync(cart, items, includeShipping: false);
            groupTotals.Add(new PreorderGroupTotal(group.Rule.Id, totals.SubTotal, totals.TaxAmount, totals.DiscountAmount, totals.ExtraChargesTotal, totals.ExtraChargeLines, totals.GrandTotal));
            grand += totals.GrandTotal;
        }
        return new PreorderTotalsBreakdown(grand, groupTotals);
    }

    /// <summary>The combined charge for every preorder group currently
    /// pending in this cart — used by CreateRazorpayOrderAsync's
    /// preorderOnly path so the real-money charge always equals the sum of
    /// whatever Order rows PlacePreorderOrdersAsync ends up creating (one
    /// per group, per your team's "one combined payment for all preorder
    /// groups" decision). Zero if the cart has no preorder items.</summary>
    public async Task<decimal> ComputePreorderGrandTotalAsync(Models.Cart.Cart cart)
    {
        var breakdown = await ComputePreorderTotalsBreakdownAsync(cart);
        return breakdown.GrandTotal;
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
            .Include(c => c.PreorderSelections)
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
    /// later. Fails if online payment isn't enabled/configured.
    ///
    /// [preorderOnly] charges just the sum of the cart's pending preorder
    /// groups (phase 1 of a two-phase checkout — preorder items are
    /// online-only) instead of the whole cart; it also marks the CartPayment
    /// row so the Razorpay webhook can tell which phase a captured payment
    /// belongs to (see HandleWebhookPaymentCapturedAsync).</summary>
    public async Task<(bool ok, string message, RazorpayOrderCreationResult? result)> CreateRazorpayOrderAsync(int cartId, bool preorderOnly = false)
    {
        var settings = await _paymentSettings.GetSettingsAsync();
        if (!settings.OnlineUsable)
            return (false, "Online payment is currently unavailable.", null);

        var cart = await LoadActiveCartAsync(cartId);
        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", null);

        decimal grandTotal;
        if (preorderOnly)
        {
            grandTotal = await ComputePreorderGrandTotalAsync(cart);
            if (grandTotal <= 0)
                return (false, "There are no preorder items in your cart.", null);
        }
        else
        {
            var totals = await ComputeCartTotalsAsync(cart);
            grandTotal = totals.GrandTotal;
        }

        RazorpayService.RazorpayOrderResult order;
        try
        {
            order = await _razorpay.CreateOrderAsync(settings.RazorpayKeyId!, settings.RazorpayKeySecret!, grandTotal, "INR", $"cart-{cartId}");
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
        payment.Method = OnlineMethod;
        payment.RazorpayOrderId = order.OrderId;
        payment.RazorpayAmount = grandTotal;
        payment.IsPreorderPayment = preorderOnly;
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

    public async Task<(bool success, string message, List<OrderCreationResult> orders)> PlaceOrderAsync(
        int cartId,
        int? customerId,
        string? razorpayPaymentId = null,
        string? razorpayOrderId = null,
        string? razorpaySignature = null,
        string? guestSessionToken = null)
    {
        var cart = await LoadActiveCartAsync(cartId);
        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", new List<OrderCreationResult>());

        // Payment verification gate — the ONLY point that decides whether an
        // order gets created for an online payment. See VerifyPaymentAsync;
        // any failure here means no Order row is ever written.
        var paymentMethod = cart.Payment?.Method;
        var (paymentOk, paymentMessage, transactionId, isVerified) = await VerifyPaymentAsync(
            cart, paymentMethod, razorpayPaymentId, razorpayOrderId, razorpaySignature);
        if (!paymentOk)
            return (false, paymentMessage, new List<OrderCreationResult>());

        return await CreateOrderFromCartAsync(cart, customerId, transactionId, isVerified, razorpaySignature, guestSessionToken);
    }

    /// <summary>Gates order creation on the admin's payment configuration and
    /// (for online payments) a real, server-verified Razorpay signature.
    /// Returns ok=false with no side effects if anything doesn't check out —
    /// callers must not create an Order when this fails.</summary>
    private async Task<(bool ok, string message, string? transactionId, bool isVerified)> VerifyPaymentAsync(
        Models.Cart.Cart cart, string? paymentMethod, string? razorpayPaymentId, string? razorpayOrderId, string? razorpaySignature)
    {
        if (string.Equals(paymentMethod, CodMethod, StringComparison.OrdinalIgnoreCase))
        {
            var settings = await _paymentSettings.GetSettingsAsync();
            if (!settings.CodEnabled)
                return (false, "Cash on Delivery is currently unavailable.", null, false);
            return (true, "", null, false);
        }

        if (string.Equals(paymentMethod, OnlineMethod, StringComparison.OrdinalIgnoreCase))
        {
            var (ok, message, transactionId) = await VerifyOnlinePaymentAsync(cart, razorpayPaymentId, razorpayOrderId, razorpaySignature);
            return (ok, message, transactionId, ok);
        }

        // Unknown/unset payment method — nothing to verify against.
        return (false, "Please select a valid payment method.", null, false);
    }

    /// <summary>The online-payment branch of VerifyPaymentAsync, extracted so
    /// PlacePreorderOrdersAsync (preorder items are online-only, by
    /// construction — there's no COD branch for it to share) can call this
    /// directly without going through a payment-method string at all.</summary>
    private async Task<(bool ok, string message, string? transactionId)> VerifyOnlinePaymentAsync(
        Models.Cart.Cart cart, string? razorpayPaymentId, string? razorpayOrderId, string? razorpaySignature)
    {
        var settings = await _paymentSettings.GetSettingsAsync();
        if (!settings.OnlineUsable)
            return (false, "Online payment is currently unavailable.", null);

        if (string.IsNullOrWhiteSpace(razorpayPaymentId) || string.IsNullOrWhiteSpace(razorpayOrderId) || string.IsNullOrWhiteSpace(razorpaySignature))
            return (false, "Payment details missing. Please complete payment before placing the order.", null);

        // The order id must be the one we ourselves created for THIS
        // cart (see ShopPaymentController.CreateOrder) — otherwise a
        // valid signature from an unrelated payment could be replayed.
        if (!string.Equals(cart.Payment?.RazorpayOrderId, razorpayOrderId, StringComparison.Ordinal))
            return (false, "Payment order mismatch. Please retry payment.", null);

        if (!_razorpay.VerifyPaymentSignature(razorpayOrderId, razorpayPaymentId, razorpaySignature, settings.RazorpayKeySecret!))
            return (false, "Payment verification failed. Order was not placed.", null);

        return (true, "", razorpayPaymentId);
    }

    /// <summary>Builds and saves the Order from an already cart — shared by
    /// the normal (client-verified) PlaceOrderAsync path and the Razorpay
    /// webhook's fallback path (HandleWebhookPaymentCapturedAsync), so both
    /// go through identical order-creation logic. [signature] is the raw
    /// razorpay_signature submitted by the client — null for the webhook
    /// path, which has no such field (its authenticity comes from the
    /// webhook's own X-Razorpay-Signature header, checked separately).</summary>
    private async Task<(bool success, string message, List<OrderCreationResult> orders)> CreateOrderFromCartAsync(
        Models.Cart.Cart cart, int? customerId, string? transactionId, bool isVerified, string? signature, string? guestSessionToken)
    {
        var cartId = cart.Id;
        var noOrders = new List<OrderCreationResult>();

        // Service-area guard — re-checked here (not just at address-save
        // time) as the authoritative gate, in case the allowlist changed
        // between the two steps or this cart's address predates it.
        var shippingOrBillingAddress = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing");
        if (!await _serviceArea.IsPincodeServiceableAsync(shippingOrBillingAddress?.Postcode))
            return (false, await _serviceArea.BuildUnserviceableMessageAsync(), noOrders);

        // A cart that still has preorder items must go through
        // PlacePreorderOrdersAsync first — preorder items are online-only
        // and always placed as their own order(s), separately from whatever
        // regular items remain. This method only ever builds the
        // regular-items order.
        var preorderResolved = await _preorder.ResolveForCartAsync(cart, shippingOrBillingAddress?.Postcode);
        if (preorderResolved.RequiresPreorder)
            return (false, "Please place your preorder items first — they're paid for and delivered separately.", noOrders);

        // Minimum order value guard — reads from core_config so ops can
        // change the threshold without a code deploy. Deliberately based on
        // merchandise subtotal only (not extra charges or shipping) — same
        // basis the cart screen's own eligibility check uses, so a cart that
        // clears the bar there can't turn around and get rejected here.
        // Skipped when this cart already cleared it once during an earlier
        // preorder phase (see PlacePreorderOrdersAsync) — the leftover
        // regular order shouldn't be retroactively rejected just because
        // the preorder portion was carved out and placed first.
        if (cart.PreorderPhaseCompletedAt == null)
        {
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
                    return (false, $"Minimum order value is {PriceFormatter.Format(minOrder)}. Please add {PriceFormatter.Format(minOrder - cartTotal)} more to proceed.", noOrders);
            }
        }

        // Cart.CustomerFirstName/LastName are never populated anywhere in the checkout
        // flow — the shipping (falling back to billing) address is the only reliable
        // source for the customer's name at this point, so use it if the cart fields
        // are blank (they always are today).
        var cartAddresses = await _db.Addresses.Where(a => a.CartId == cartId).ToListAsync();
        var nameSourceAddress = cartAddresses.FirstOrDefault(a => a.AddressType == "cart_shipping")
            ?? cartAddresses.FirstOrDefault(a => a.AddressType == "cart_billing")
            ?? cartAddresses.FirstOrDefault();

        var order = await BuildAndSaveOrderGroupAsync(
            cart, cart.Items, preorderRule: null, preorderDeliveryDate: null, preorderSlot: null,
            customerId, transactionId, isVerified, signature, nameSourceAddress, cartAddresses);

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

        var result = new OrderCreationResult(order.Id, order.IncrementId!, false, order.GrandTotal ?? 0m);

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
        var orderGrandTotal = order.GrandTotal;
        var pushCustomerId = customerId ?? cart.CustomerId;
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
                    CustomerId = pushCustomerId,
                    GrandTotal = orderGrandTotal,
                };
                if (pushCustomerId.HasValue && pushCustomerId.Value > 0)
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

        return (true, "Order placed successfully.", new List<OrderCreationResult> { result });
    }

    /// <summary>Builds, saves, and returns every preorder Order this cart's
    /// pending preorder groups produce — one order per distinct rule (see
    /// PreorderService.CartPreorderResolution.Groups) — using a single
    /// combined online payment for all of them together. Removes only the
    /// consumed items/selections from the cart, leaving any regular items
    /// behind for a later, separate CreateOrderFromCartAsync call. Shared by
    /// PlacePreorderOrdersAsync (client-verified) and the Razorpay webhook's
    /// fallback path.</summary>
    private async Task<(bool success, string message, List<OrderCreationResult> orders)> BuildPreorderOrdersFromCartAsync(
        Models.Cart.Cart cart, int? customerId, string? transactionId, bool isVerified, string? signature, string? guestSessionToken)
    {
        var noOrders = new List<OrderCreationResult>();

        var shippingOrBillingAddress = cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_shipping")
            ?? cart.Addresses.FirstOrDefault(a => a.AddressType == "cart_billing");
        if (!await _serviceArea.IsPincodeServiceableAsync(shippingOrBillingAddress?.Postcode))
            return (false, await _serviceArea.BuildUnserviceableMessageAsync(), noOrders);

        // Minimum order value — the full combined cart (preorder + whatever
        // regular items are still in it), same basis/reasoning as
        // CreateOrderFromCartAsync's guard. This is always the FIRST gate a
        // mixed or pure-preorder cart hits, since preorder always places
        // first — PreorderPhaseCompletedAt is set below once this succeeds,
        // so the later regular-order call knows to skip re-checking it.
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
                return (false, $"Minimum order value is {PriceFormatter.Format(minOrder)}. Please add {PriceFormatter.Format(minOrder - cartTotal)} more to proceed.", noOrders);
        }

        var preorderResolved = await _preorder.ResolveForCartAsync(cart, shippingOrBillingAddress?.Postcode);
        if (!preorderResolved.RequiresPreorder)
            return (false, "There are no preorder items in your cart.", noOrders);

        var groups = GroupPreorderItems(cart.Items, preorderResolved);
        var today = PreorderService.TodayIst();
        var chosenPerGroup = new List<(PreorderService.CartPreorderGroup Group, List<Models.Cart.CartItem> Items, DateTime Date, Models.Catalog.PreorderSlot Slot)>();

        foreach (var (group, items) in groups)
        {
            var selection = cart.PreorderSelections.FirstOrDefault(s => s.PreorderRuleId == group.Rule.Id);
            if (selection == null)
                return (false, "Please choose a delivery slot for all your preorder items.", noOrders);

            var windowDays = group.Rule.WindowDays ?? PreorderService.DefaultWindowDays;
            var minLeadHours = group.Rule.MinLeadHours ?? PreorderService.DefaultMinLeadHours;
            var chosenDate = selection.DeliveryDate.Date;
            if (chosenDate < today || chosenDate > today.AddDays(windowDays - 1))
                return (false, "One of your chosen delivery dates is no longer available. Please choose a new delivery slot.", noOrders);

            var chosenSlot = group.Slots.FirstOrDefault(s => s.Id == selection.SlotId);
            if (chosenSlot == null)
                return (false, "One of your chosen delivery time slots is no longer available. Please choose a new delivery slot.", noOrders);

            if (!PreorderService.IsSlotSelectable(chosenDate, chosenSlot, minLeadHours))
                return (false, "One of your chosen delivery slots no longer allows enough lead time. Please choose a new delivery slot.", noOrders);

            chosenPerGroup.Add((group, items, chosenDate, chosenSlot));
        }

        var cartAddresses = await _db.Addresses.Where(a => a.CartId == cart.Id).ToListAsync();
        var nameSourceAddress = cartAddresses.FirstOrDefault(a => a.AddressType == "cart_shipping")
            ?? cartAddresses.FirstOrDefault(a => a.AddressType == "cart_billing")
            ?? cartAddresses.FirstOrDefault();

        // N preorder orders sharing one checkout should commit or fail
        // together. The DbContext's configured retrying execution strategy
        // (MySqlRetryingExecutionStrategy) refuses a bare
        // BeginTransactionAsync — a user-managed transaction must run
        // through CreateExecutionStrategy().ExecuteAsync so a transient
        // failure retries the whole block, not just one query.
        var strategy = _db.Database.CreateExecutionStrategy();
        var results = await strategy.ExecuteAsync(async () =>
        {
            var groupResults = new List<OrderCreationResult>();
            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var allPreorderItems = new List<Models.Cart.CartItem>();
                foreach (var (group, items, date, slot) in chosenPerGroup)
                {
                    var order = await BuildAndSaveOrderGroupAsync(
                        cart, items, group.Rule, date, slot,
                        customerId, transactionId, isVerified, signature, nameSourceAddress, cartAddresses);
                    groupResults.Add(new OrderCreationResult(order.Id, order.IncrementId!, true, order.GrandTotal ?? 0m));
                    allPreorderItems.AddRange(items);
                }

                // Remove only the consumed items — any regular items stay in
                // the cart for a later, separate CreateOrderFromCartAsync call.
                _db.CartItems.RemoveRange(allPreorderItems);
                var consumedItemIds = allPreorderItems.Select(i => i.Id).ToHashSet();
                var remainingItems = cart.Items.Where(i => !consumedItemIds.Contains(i.Id)).ToList();
                cart.ItemsCount = remainingItems.Count;
                cart.ItemsQty = remainingItems.Sum(i => i.Quantity);
                cart.IsActive = remainingItems.Count > 0;
                cart.PreorderPhaseCompletedAt = DateTime.UtcNow;
                cart.UpdatedAt = DateTime.UtcNow;

                // Deduct inventory for just the preorder items.
                var productIds = allPreorderItems.Select(ci => ci.ProductId).Distinct().ToList();
                var inventories = (await _db.ProductInventories
                    .Where(i => productIds.Contains(i.ProductId))
                    .ToListAsync())
                    .GroupBy(i => i.ProductId)
                    .ToDictionary(g => g.Key, g => g.First());
                foreach (var ci in allPreorderItems)
                {
                    if (inventories.TryGetValue(ci.ProductId, out var inv))
                        inv.Qty = Math.Max(0, inv.Qty - ci.Quantity);
                }

                // Consume the selections for the groups just placed.
                var placedRuleIds = chosenPerGroup.Select(g => g.Group.Rule.Id).ToHashSet();
                var consumedSelections = cart.PreorderSelections.Where(s => placedRuleIds.Contains(s.PreorderRuleId)).ToList();
                _db.CartPreorderSelections.RemoveRange(consumedSelections);

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                return groupResults;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // Fire-and-forget the order-placed push, once per created order —
        // same reasoning/shape as CreateOrderFromCartAsync's notification block.
        var pushCustomerId = customerId ?? cart.CustomerId;
        foreach (var r in results)
        {
            var orderId = r.OrderId;
            var orderIncrementId = r.IncrementId;
            var orderGrandTotal = r.GrandTotal;
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
                        CustomerId = pushCustomerId,
                        GrandTotal = orderGrandTotal,
                    };
                    if (pushCustomerId.HasValue && pushCustomerId.Value > 0)
                        await notify.SendOrderPlacedAsync(pushOrder);
                    else if (!string.IsNullOrWhiteSpace(guestSessionToken))
                        await notify.SendGuestOrderPlacedAsync(pushOrder, guestSessionToken);

                    await notify.SendOrderPlacedToAdminsAsync(pushOrder);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[Checkout] Preorder order placed but push failed (orderId={OrderId})", orderId);
                }
            });
        }

        var message = results.Count > 1
            ? $"{results.Count} preorder orders placed successfully — your items ship on different delivery dates."
            : "Preorder order placed successfully.";
        return (true, message, results);
    }

    /// <summary>Public entry point for placing a cart's preorder items —
    /// phase 1 of a two-phase checkout. Preorder items are online-only by
    /// design, so this never accepts a payment method: it verifies a real
    /// Razorpay payment directly (see VerifyOnlinePaymentAsync) and, once
    /// confirmed, builds every pending preorder group into its own Order.</summary>
    public async Task<(bool success, string message, List<OrderCreationResult> orders)> PlacePreorderOrdersAsync(
        int cartId, int? customerId, string? razorpayPaymentId, string? razorpayOrderId, string? razorpaySignature, string? guestSessionToken)
    {
        var cart = await LoadActiveCartAsync(cartId);
        if (cart == null || !cart.Items.Any())
            return (false, "Cart is empty or not found.", new List<OrderCreationResult>());

        var (paymentOk, paymentMessage, transactionId) = await VerifyOnlinePaymentAsync(cart, razorpayPaymentId, razorpayOrderId, razorpaySignature);
        if (!paymentOk)
            return (false, paymentMessage, new List<OrderCreationResult>());

        return await BuildPreorderOrdersFromCartAsync(cart, customerId, transactionId, isVerified: true, razorpaySignature, guestSessionToken);
    }

    /// <summary>Builds, saves, and returns one Order for one group of a
    /// cart's items — the shared builder for both the plain regular-items
    /// order (CreateOrderFromCartAsync, called with preorderRule: null) and
    /// each individual preorder group's order (BuildPreorderOrdersFromCartAsync,
    /// called once per distinct rule, each with its own date/slot). Taking
    /// the rule/date/slot as explicit parameters — rather than reading a
    /// single shared value off the cart — is what lets two preorder groups
    /// with two different rules/slots each get their own correct order.</summary>
    private async Task<Order> BuildAndSaveOrderGroupAsync(
        Models.Cart.Cart cart, List<Models.Cart.CartItem> items,
        Models.Catalog.PreorderRule? preorderRule, DateTime? preorderDeliveryDate, Models.Catalog.PreorderSlot? preorderSlot,
        int? customerId, string? transactionId, bool isVerified, string? signature,
        Address? nameSourceAddress, List<Address> cartAddresses)
    {
        var lastOrder = await _db.Orders.OrderByDescending(o => o.Id).FirstOrDefaultAsync();
        var incrementId = lastOrder != null
            ? (int.Parse(lastOrder.IncrementId ?? "100000") + 1).ToString()
            : "100001";

        // No shipping charge for a preorder group — see ComputeGroupOrderTotalsAsync.
        var groupTotals = await ComputeGroupOrderTotalsAsync(cart, items, includeShipping: preorderRule == null);

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
            ShippingTitle = groupTotals.ShippingTitle,
            ShippingDescription = groupTotals.ShippingDescription,
            CouponCode = cart.CouponCode,
            TotalItemCount = items.Count,
            TotalQtyOrdered = items.Sum(i => i.Quantity),
            BaseCurrencyCode = cart.BaseCurrencyCode ?? "INR",
            ChannelCurrencyCode = cart.ChannelCurrencyCode ?? "INR",
            OrderCurrencyCode = cart.CartCurrencyCode ?? "INR",
            GrandTotal = groupTotals.GrandTotal,
            BaseGrandTotal = groupTotals.GrandTotal,
            ExtraChargesTotal = groupTotals.ExtraChargesTotal,
            SubTotal = groupTotals.SubTotal,
            BaseSubTotal = groupTotals.SubTotal,
            TaxAmount = groupTotals.TaxAmount,
            BaseTaxAmount = groupTotals.TaxAmount,
            DiscountAmount = groupTotals.DiscountAmount,
            BaseDiscountAmount = groupTotals.DiscountAmount,
            ShippingAmount = groupTotals.ShippingAmount,
            BaseShippingAmount = groupTotals.ShippingAmount,
            CustomerId = customerId ?? cart.CustomerId,
            ChannelId = cart.ChannelId,
            CartId = cart.Id,
            AppliedCartRuleIds = cart.AppliedCartRuleIds,
            IsPreorder = preorderRule != null,
            PreorderDeliveryDate = preorderRule != null ? preorderDeliveryDate : null,
            PreorderSlotId = preorderRule != null ? preorderSlot?.Id : null,
            PreorderSlotLabel = preorderRule != null && preorderSlot != null ? FormatSlotLabel(preorderSlot) : null,
            PreorderRuleId = preorderRule?.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        for (int i = 0; i < groupTotals.ExtraChargeLines.Count; i++)
        {
            var line = groupTotals.ExtraChargeLines[i];
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

        foreach (var ci in items)
        {
            _db.OrderItems.Add(new OrderItem
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
            });
        }

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

        await _db.SaveChangesAsync();
        return order;
    }

    /// <summary>Safety-net path for ShopPaymentController's Razorpay webhook:
    /// a `payment.captured` event already proves the payment is real (the
    /// webhook signature check is the authentication for this call — no
    /// separate checkout-signature check needed here). Finishes placing the
    /// order for a cart whose client never completed its own verify+place
    /// call (e.g. app closed right after paying). No-ops if the cart was
    /// already converted to an order by the normal client path.
    ///
    /// CartPayment.IsPreorderPayment (set by CreateRazorpayOrderAsync) says
    /// which phase this captured payment was for, so it's routed to the
    /// matching builder — a preorder phase 1 payment must never fall through
    /// to CreateOrderFromCartAsync, since that path now rejects any cart
    /// that still has preorder items.</summary>
    public async Task<(bool success, string message, List<OrderCreationResult> orders)> HandleWebhookPaymentCapturedAsync(
        string razorpayOrderId, string razorpayPaymentId)
    {
        var cartPayment = await _db.CartPayments.FirstOrDefaultAsync(p => p.RazorpayOrderId == razorpayOrderId);
        if (cartPayment?.CartId == null)
            return (false, "No cart found for this Razorpay order.", new List<OrderCreationResult>());

        var cart = await LoadActiveCartAsync(cartPayment.CartId.Value);
        if (cart == null || !cart.Items.Any())
            return (true, "Order already placed for this cart.", new List<OrderCreationResult>());

        if (cartPayment.IsPreorderPayment)
            return await BuildPreorderOrdersFromCartAsync(cart, cart.CustomerId, razorpayPaymentId, isVerified: true, signature: null, guestSessionToken: null);

        return await CreateOrderFromCartAsync(cart, cart.CustomerId, razorpayPaymentId, isVerified: true, signature: null, guestSessionToken: null);
    }
}

/// <summary>One resulting Order from a checkout — a checkout that didn't
/// need splitting returns exactly one of these; a mixed cart returns two
/// (one regular, one preorder). Callers always handle a list, never a
/// nullable scalar, so there's a single contract either way.</summary>
public record OrderCreationResult(int OrderId, string IncrementId, bool IsPreorder, decimal GrandTotal);

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
