using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DOSApi.Models.Customer;

namespace DOSApi.Models.Cart;

[Table("cart")]
public class Cart
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("customer_email")]
    public string? CustomerEmail { get; set; }

    [Column("customer_first_name")]
    public string? CustomerFirstName { get; set; }

    [Column("customer_last_name")]
    public string? CustomerLastName { get; set; }

    [Column("shipping_method")]
    public string? ShippingMethod { get; set; }

    [Column("coupon_code")]
    public string? CouponCode { get; set; }

    [Column("is_gift")]
    public bool IsGift { get; set; }

    [Column("items_count")]
    public int? ItemsCount { get; set; }

    [Column("items_qty")]
    public decimal? ItemsQty { get; set; }

    [Column("exchange_rate")]
    public decimal? ExchangeRate { get; set; }

    [Column("global_currency_code")]
    public string? GlobalCurrencyCode { get; set; }

    [Column("base_currency_code")]
    public string? BaseCurrencyCode { get; set; }

    [Column("channel_currency_code")]
    public string? ChannelCurrencyCode { get; set; }

    [Column("cart_currency_code")]
    public string? CartCurrencyCode { get; set; }

    [Column("grand_total")]
    public decimal? GrandTotal { get; set; }

    [Column("base_grand_total")]
    public decimal? BaseGrandTotal { get; set; }

    [Column("sub_total")]
    public decimal? SubTotal { get; set; }

    [Column("base_sub_total")]
    public decimal? BaseSubTotal { get; set; }

    [Column("tax_total")]
    public decimal? TaxTotal { get; set; }

    [Column("base_tax_total")]
    public decimal? BaseTaxTotal { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("checkout_method")]
    public string? CheckoutMethod { get; set; }

    [Column("is_guest")]
    public bool? IsGuest { get; set; }

    [Column("is_active")]
    public bool? IsActive { get; set; } = true;

    [Column("applied_cart_rule_ids")]
    public string? AppliedCartRuleIds { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("channel_id")]
    public int ChannelId { get; set; }

    // Customer-chosen delivery date/slot for this cart, when at least one
    // item is under preorder — set via CheckoutService.SavePreorderSelectionAsync,
    // re-validated (not just trusted) at order placement — see
    // CheckoutService.CreateOrderFromCartAsync. Date-only value stored in a
    // DateTime (time component unused) to match every other date column in
    // this codebase — no DateOnly is used anywhere else here.
    [Column("preorder_delivery_date")]
    public DateTime? PreorderDeliveryDate { get; set; }

    [Column("preorder_slot_id")]
    public int? PreorderSlotId { get; set; }

    // Set once PlacePreorderOrdersAsync finishes carving a cart's preorder
    // items out into their own order(s) — lets the later regular-item
    // placement skip re-checking the minimum-order-value gate, since the
    // combined cart (preorder + regular together) already cleared it once,
    // and the leftover regular order shouldn't be retroactively rejected
    // just because the preorder portion was placed first.
    [Column("preorder_phase_completed_at")]
    public DateTime? PreorderPhaseCompletedAt { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Models.Customer.Customer? Customer { get; set; }
    public List<CartItem> Items { get; set; } = new();
    public CartPayment? Payment { get; set; }
    public List<CartShippingRate> ShippingRates { get; set; } = new();
    public List<Address> Addresses { get; set; } = new();
    public List<CartPreorderSelection> PreorderSelections { get; set; } = new();
}

[Table("cart_items")]
public class CartItem
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("sku")]
    public string? Sku { get; set; }

    [Column("type")]
    public string? Type { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("coupon_code")]
    public string? CouponCode { get; set; }

    [Column("weight")]
    public decimal Weight { get; set; }

    [Column("total_weight")]
    public decimal TotalWeight { get; set; }

    [Column("base_total_weight")]
    public decimal BaseTotalWeight { get; set; }

    [Column("price")]
    public decimal Price { get; set; }

    [Column("base_price")]
    public decimal BasePrice { get; set; }

    [Column("custom_price")]
    public decimal? CustomPrice { get; set; }

    [Column("total")]
    public decimal Total { get; set; }

    [Column("base_total")]
    public decimal BaseTotal { get; set; }

    [Column("tax_percent")]
    public decimal? TaxPercent { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("discount_percent")]
    public decimal DiscountPercent { get; set; }

    [Column("discount_amount")]
    public decimal DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal BaseDiscountAmount { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("cart_id")]
    public int CartId { get; set; }

    [Column("tax_category_id")]
    public int? TaxCategoryId { get; set; }

    [Column("applied_cart_rule_ids")]
    public string? AppliedCartRuleIds { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Cart? Cart { get; set; }
    public Catalog.Product? Product { get; set; }
    public CartItem? ParentItem { get; set; }
    public List<CartItem> ChildItems { get; set; } = new();
}

[Table("cart_payment")]
public class CartPayment
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("method")]
    public string Method { get; set; } = "";

    [Column("method_title")]
    public string? MethodTitle { get; set; }

    [Column("cart_id")]
    public int? CartId { get; set; }

    // Set by ShopPaymentController when it creates a real Razorpay order for
    // this cart — PlaceOrderAsync cross-checks the client-submitted
    // razorpayOrderId against this before trusting a payment signature.
    [Column("razorpay_order_id")]
    public string? RazorpayOrderId { get; set; }

    [Column("razorpay_amount")]
    public decimal? RazorpayAmount { get; set; }

    // Set by CheckoutService.CreateRazorpayOrderAsync(preorderOnly: true) —
    // lets the Razorpay webhook (HandleWebhookPaymentCapturedAsync) tell
    // whether a captured payment was for a cart's preorder phase (route to
    // PlacePreorderOrdersAsync) or its regular-item phase (route to
    // CreateOrderFromCartAsync) when the client's own verify+place call
    // never completed.
    [Column("is_preorder_payment")]
    public bool IsPreorderPayment { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Cart? Cart { get; set; }
}

/// <summary>One customer-chosen delivery date+slot for one distinct
/// preorder rule within a cart — a cart can have several of these
/// simultaneously (see PreorderService.CartPreorderResolution.Groups),
/// since two preorder items can belong to two different rules with
/// different delivery windows. Consumed (deleted) once its group's order
/// is placed via CheckoutService.PlacePreorderOrdersAsync.</summary>
[Table("cart_preorder_selections")]
public class CartPreorderSelection
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("cart_id")]
    public int CartId { get; set; }

    [Column("preorder_rule_id")]
    public int PreorderRuleId { get; set; }

    [Column("delivery_date")]
    public DateTime DeliveryDate { get; set; }

    [Column("slot_id")]
    public int SlotId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Cart? Cart { get; set; }
}

[Table("cart_shipping_rates")]
public class CartShippingRate
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("carrier")]
    public string Carrier { get; set; } = "";

    [Column("carrier_title")]
    public string CarrierTitle { get; set; } = "";

    [Column("method")]
    public string Method { get; set; } = "";

    [Column("method_title")]
    public string MethodTitle { get; set; } = "";

    [Column("method_description")]
    public string? MethodDescription { get; set; }

    [Column("price")]
    public double? Price { get; set; }

    [Column("base_price")]
    public double? BasePrice { get; set; }

    [Column("discount_amount")]
    public decimal DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal BaseDiscountAmount { get; set; }

    [Column("is_calculate_tax")]
    public bool IsCalculateTax { get; set; } = true;

    [Column("cart_address_id")]
    public int? CartAddressId { get; set; }

    [Column("cart_id")]
    public int? CartId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Cart? Cart { get; set; }
}
