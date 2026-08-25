using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DOSApi.Models.Customer;
using DOSApi.Models.Catalog;

namespace DOSApi.Models.Sales;

[Table("orders")]
public class Order
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("increment_id")]
    public string? IncrementId { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("channel_name")]
    public string? ChannelName { get; set; }

    [Column("is_guest")]
    public bool? IsGuest { get; set; }

    [Column("customer_email")]
    public string? CustomerEmail { get; set; }

    [Column("customer_first_name")]
    public string? CustomerFirstName { get; set; }

    [Column("customer_last_name")]
    public string? CustomerLastName { get; set; }

    [Column("shipping_method")]
    public string? ShippingMethod { get; set; }

    [Column("shipping_title")]
    public string? ShippingTitle { get; set; }

    [Column("shipping_description")]
    public string? ShippingDescription { get; set; }

    [Column("coupon_code")]
    public string? CouponCode { get; set; }

    [Column("is_gift")]
    public bool IsGift { get; set; }

    [Column("total_item_count")]
    public int? TotalItemCount { get; set; }

    [Column("total_qty_ordered")]
    public int? TotalQtyOrdered { get; set; }

    [Column("base_currency_code")]
    public string? BaseCurrencyCode { get; set; }

    [Column("channel_currency_code")]
    public string? ChannelCurrencyCode { get; set; }

    [Column("order_currency_code")]
    public string? OrderCurrencyCode { get; set; }

    [Column("grand_total")]
    public decimal? GrandTotal { get; set; }

    [Column("base_grand_total")]
    public decimal? BaseGrandTotal { get; set; }

    /// <summary>Sum of active ExtraCharges applied at placement time,
    /// already folded into GrandTotal — kept separately for audit/refund
    /// correctness. See ExtraChargeService.</summary>
    [Column("extra_charges_total")]
    public decimal? ExtraChargesTotal { get; set; }

    [Column("sub_total")]
    public decimal? SubTotal { get; set; }

    [Column("base_sub_total")]
    public decimal? BaseSubTotal { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("shipping_amount")]
    public decimal? ShippingAmount { get; set; }

    [Column("base_shipping_amount")]
    public decimal? BaseShippingAmount { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("grand_total_invoiced")]
    public decimal? GrandTotalInvoiced { get; set; }

    [Column("base_grand_total_invoiced")]
    public decimal? BaseGrandTotalInvoiced { get; set; }

    [Column("grand_total_refunded")]
    public decimal? GrandTotalRefunded { get; set; }

    [Column("base_grand_total_refunded")]
    public decimal? BaseGrandTotalRefunded { get; set; }

    [Column("sub_total_invoiced")]
    public decimal? SubTotalInvoiced { get; set; }

    [Column("base_sub_total_invoiced")]
    public decimal? BaseSubTotalInvoiced { get; set; }

    [Column("sub_total_refunded")]
    public decimal? SubTotalRefunded { get; set; }

    [Column("base_sub_total_refunded")]
    public decimal? BaseSubTotalRefunded { get; set; }

    [Column("discount_invoiced")]
    public decimal? DiscountInvoiced { get; set; }

    [Column("base_discount_invoiced")]
    public decimal? BaseDiscountInvoiced { get; set; }

    [Column("discount_refunded")]
    public decimal? DiscountRefunded { get; set; }

    [Column("base_discount_refunded")]
    public decimal? BaseDiscountRefunded { get; set; }

    [Column("tax_amount_invoiced")]
    public decimal? TaxAmountInvoiced { get; set; }

    [Column("base_tax_amount_invoiced")]
    public decimal? BaseTaxAmountInvoiced { get; set; }

    [Column("tax_amount_refunded")]
    public decimal? TaxAmountRefunded { get; set; }

    [Column("base_tax_amount_refunded")]
    public decimal? BaseTaxAmountRefunded { get; set; }

    [Column("shipping_invoiced")]
    public decimal? ShippingInvoiced { get; set; }

    [Column("base_shipping_invoiced")]
    public decimal? BaseShippingInvoiced { get; set; }

    [Column("shipping_refunded")]
    public decimal? ShippingRefunded { get; set; }

    [Column("base_shipping_refunded")]
    public decimal? BaseShippingRefunded { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("customer_type")]
    public string? CustomerType { get; set; }

    [Column("channel_id")]
    public int? ChannelId { get; set; }

    [Column("channel_type")]
    public string? ChannelType { get; set; }

    [Column("cart_id")]
    public int? CartId { get; set; }

    [Column("applied_cart_rule_ids")]
    public string? AppliedCartRuleIds { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Set once, the first time this order's status reaches
    /// "completed" — powers the refund window (see
    /// AccountService.RequestRefundAsync). Null for orders that haven't
    /// been delivered yet, or that reached "completed" before this column
    /// existed.</summary>
    [Column("delivered_at")]
    public DateTime? DeliveredAt { get; set; }

    // Preorder snapshot — copied from the cart at placement time by
    // CheckoutService.CreateOrderFromCartAsync, after re-validating the
    // selection is still good. PreorderSlotLabel is a resolved-string
    // snapshot (e.g. "9 AM - 12 PM"), same convention as ShippingTitle/
    // ShippingDescription, so the order still reads correctly even if the
    // slot is later renamed/deleted. PreorderRuleId is audit-only.
    [Column("is_preorder")]
    public bool IsPreorder { get; set; }

    [Column("preorder_delivery_date")]
    public DateTime? PreorderDeliveryDate { get; set; }

    [Column("preorder_slot_id")]
    public int? PreorderSlotId { get; set; }

    [Column("preorder_slot_label")]
    public string? PreorderSlotLabel { get; set; }

    [Column("preorder_rule_id")]
    public int? PreorderRuleId { get; set; }

    // Navigation
    public Models.Customer.Customer? Customer { get; set; }
    public List<OrderItem> Items { get; set; } = new();
    public List<Address> Addresses { get; set; } = new();
    public OrderPayment? Payment { get; set; }
    public List<Invoice> Invoices { get; set; } = new();
    public List<Shipment> Shipments { get; set; } = new();
    public List<Refund> Refunds { get; set; } = new();
    public List<OrderExtraCharge> ExtraCharges { get; set; } = new();
}

[Table("order_items")]
public class OrderItem
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("sku")]
    public string? Sku { get; set; }

    [Column("type")]
    public string? Type { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("coupon_code")]
    public string? CouponCode { get; set; }

    [Column("weight")]
    public decimal? Weight { get; set; }

    [Column("total_weight")]
    public decimal? TotalWeight { get; set; }

    [Column("qty_ordered")]
    public int? QtyOrdered { get; set; }

    [Column("qty_shipped")]
    public int? QtyShipped { get; set; }

    [Column("qty_invoiced")]
    public int? QtyInvoiced { get; set; }

    [Column("qty_canceled")]
    public int? QtyCanceled { get; set; }

    [Column("qty_refunded")]
    public int? QtyRefunded { get; set; }

    [Column("price")]
    public decimal? Price { get; set; }

    [Column("base_price")]
    public decimal? BasePrice { get; set; }

    [Column("total")]
    public decimal? Total { get; set; }

    [Column("base_total")]
    public decimal? BaseTotal { get; set; }

    [Column("tax_percent")]
    public decimal? TaxPercent { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("discount_percent")]
    public decimal? DiscountPercent { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("product_id")]
    public int? ProductId { get; set; }

    [Column("product_type")]
    public string? ProductType { get; set; }

    [Column("order_id")]
    public int? OrderId { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("tax_category_id")]
    public int? TaxCategoryId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Product? Product { get; set; }
    public Order? Order { get; set; }
    public OrderItem? ParentItem { get; set; }
    public List<OrderItem> ChildItems { get; set; } = new();
}

[Table("order_payment")]
public class OrderPayment
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("order_id")]
    public int? OrderId { get; set; }

    [Column("method")]
    public string Method { get; set; } = "";

    [Column("method_title")]
    public string? MethodTitle { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    // The Razorpay payment id (for online payments) — set only once
    // CheckoutService has verified the payment signature server-side.
    [Column("transaction_id")]
    public string? TransactionId { get; set; }

    // True for a verified online payment. COD orders stay false — there's no
    // upfront payment to verify — which is fine since nothing gates on this
    // for COD; it only matters for distinguishing a real Razorpay capture.
    [Column("is_verified")]
    public bool IsVerified { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Order? Order { get; set; }
}

/// <summary>Itemized extra-charge line as it was actually resolved at order
/// placement (Handling Charges, Processing Fee, Cold Chain Fee, etc. — see
/// ExtraChargeService.ComputeAsync) — snapshotted so it stays historically
/// accurate even if the admin later edits/removes the extra_charges config
/// row it came from. orders.extra_charges_total is the sum of these.</summary>
[Table("order_extra_charges")]
public class OrderExtraCharge
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("charge_type")]
    public string ChargeType { get; set; } = "fixed";

    [Column("rate")]
    public decimal Rate { get; set; }

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    public Order? Order { get; set; }
}

[Table("invoices")]
public class Invoice
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("increment_id")]
    public string? IncrementId { get; set; }

    [Column("state")]
    public string? State { get; set; }

    [Column("email_sent")]
    public bool EmailSent { get; set; }

    [Column("total_qty")]
    public int? TotalQty { get; set; }

    [Column("base_currency_code")]
    public string? BaseCurrencyCode { get; set; }

    [Column("channel_currency_code")]
    public string? ChannelCurrencyCode { get; set; }

    [Column("order_currency_code")]
    public string? OrderCurrencyCode { get; set; }

    [Column("sub_total")]
    public decimal? SubTotal { get; set; }

    [Column("base_sub_total")]
    public decimal? BaseSubTotal { get; set; }

    [Column("grand_total")]
    public decimal? GrandTotal { get; set; }

    [Column("base_grand_total")]
    public decimal? BaseGrandTotal { get; set; }

    [Column("shipping_amount")]
    public decimal? ShippingAmount { get; set; }

    [Column("base_shipping_amount")]
    public decimal? BaseShippingAmount { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("order_id")]
    public int? OrderId { get; set; }

    [Column("transaction_id")]
    public string? TransactionId { get; set; }

    [Column("reminders")]
    public int Reminders { get; set; }

    [Column("next_reminder_at")]
    public DateTime? NextReminderAt { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Order? Order { get; set; }
    public List<InvoiceItem> Items { get; set; } = new();
}

[Table("invoice_items")]
public class InvoiceItem
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("sku")]
    public string? Sku { get; set; }

    [Column("qty")]
    public int? Qty { get; set; }

    [Column("price")]
    public decimal? Price { get; set; }

    [Column("base_price")]
    public decimal? BasePrice { get; set; }

    [Column("total")]
    public decimal? Total { get; set; }

    [Column("base_total")]
    public decimal? BaseTotal { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("discount_percent")]
    public decimal? DiscountPercent { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("price_incl_tax")]
    public decimal? PriceInclTax { get; set; }

    [Column("base_price_incl_tax")]
    public decimal? BasePriceInclTax { get; set; }

    [Column("total_incl_tax")]
    public decimal? TotalInclTax { get; set; }

    [Column("base_total_incl_tax")]
    public decimal? BaseTotalInclTax { get; set; }

    [Column("product_id")]
    public int? ProductId { get; set; }

    [Column("product_type")]
    public string? ProductType { get; set; }

    [Column("order_item_id")]
    public int? OrderItemId { get; set; }

    [Column("invoice_id")]
    public int? InvoiceId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Invoice? Invoice { get; set; }
}

[Table("shipments")]
public class Shipment
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("total_qty")]
    public int? TotalQty { get; set; }

    [Column("total_weight")]
    public float? TotalWeight { get; set; }

    [Column("carrier_code")]
    public string? CarrierCode { get; set; }

    [Column("carrier_title")]
    public string? CarrierTitle { get; set; }

    [Column("track_number")]
    public string? TrackNumber { get; set; }

    [Column("email_sent")]
    public bool EmailSent { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("customer_type")]
    public string? CustomerType { get; set; }

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("order_address_id")]
    public int? OrderAddressId { get; set; }

    [Column("inventory_source_id")]
    public int? InventorySourceId { get; set; }

    [Column("inventory_source_name")]
    public string? InventorySourceName { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Order? Order { get; set; }
    public List<ShipmentItem> Items { get; set; } = new();
}

[Table("shipment_items")]
public class ShipmentItem
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("sku")]
    public string? Sku { get; set; }

    [Column("qty")]
    public int? Qty { get; set; }

    [Column("weight")]
    public float? Weight { get; set; }

    [Column("price")]
    public decimal? Price { get; set; }

    [Column("base_price")]
    public decimal? BasePrice { get; set; }

    [Column("total")]
    public decimal? Total { get; set; }

    [Column("base_total")]
    public decimal? BaseTotal { get; set; }

    [Column("product_id")]
    public int? ProductId { get; set; }

    [Column("product_type")]
    public string? ProductType { get; set; }

    [Column("order_item_id")]
    public int? OrderItemId { get; set; }

    [Column("shipment_id")]
    public int ShipmentId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Shipment? Shipment { get; set; }
}

[Table("refunds")]
public class Refund
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("increment_id")]
    public string? IncrementId { get; set; }

    [Column("state")]
    public string? State { get; set; }

    [Column("email_sent")]
    public bool EmailSent { get; set; }

    [Column("total_qty")]
    public int? TotalQty { get; set; }

    [Column("base_currency_code")]
    public string? BaseCurrencyCode { get; set; }

    [Column("channel_currency_code")]
    public string? ChannelCurrencyCode { get; set; }

    [Column("order_currency_code")]
    public string? OrderCurrencyCode { get; set; }

    [Column("sub_total")]
    public decimal? SubTotal { get; set; }

    [Column("base_sub_total")]
    public decimal? BaseSubTotal { get; set; }

    [Column("grand_total")]
    public decimal? GrandTotal { get; set; }

    [Column("base_grand_total")]
    public decimal? BaseGrandTotal { get; set; }

    [Column("adjustment_refund")]
    public decimal? AdjustmentRefund { get; set; }

    [Column("base_adjustment_refund")]
    public decimal? BaseAdjustmentRefund { get; set; }

    [Column("adjustment_fee")]
    public decimal? AdjustmentFee { get; set; }

    [Column("base_adjustment_fee")]
    public decimal? BaseAdjustmentFee { get; set; }

    [Column("shipping_amount")]
    public decimal? ShippingAmount { get; set; }

    [Column("base_shipping_amount")]
    public decimal? BaseShippingAmount { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("order_id")]
    public int? OrderId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Order? Order { get; set; }
    public List<RefundItem> Items { get; set; } = new();
}

[Table("refund_items")]
public class RefundItem
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("sku")]
    public string? Sku { get; set; }

    [Column("qty")]
    public int? Qty { get; set; }

    [Column("price")]
    public decimal? Price { get; set; }

    [Column("base_price")]
    public decimal? BasePrice { get; set; }

    [Column("total")]
    public decimal? Total { get; set; }

    [Column("base_total")]
    public decimal? BaseTotal { get; set; }

    [Column("tax_amount")]
    public decimal? TaxAmount { get; set; }

    [Column("base_tax_amount")]
    public decimal? BaseTaxAmount { get; set; }

    [Column("discount_percent")]
    public decimal? DiscountPercent { get; set; }

    [Column("discount_amount")]
    public decimal? DiscountAmount { get; set; }

    [Column("base_discount_amount")]
    public decimal? BaseDiscountAmount { get; set; }

    [Column("product_id")]
    public int? ProductId { get; set; }

    [Column("product_type")]
    public string? ProductType { get; set; }

    [Column("order_item_id")]
    public int? OrderItemId { get; set; }

    [Column("refund_id")]
    public int? RefundId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Refund? Refund { get; set; }
}

[Table("downloadable_link_purchased")]
public class DownloadableLinkPurchased
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("product_name")]
    public string? ProductName { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("url")]
    public string? Url { get; set; }

    [Column("file")]
    public string? File { get; set; }

    [Column("file_name")]
    public string? FileName { get; set; }

    [Column("type")]
    public string Type { get; set; } = "";

    [Column("download_bought")]
    public int DownloadBought { get; set; }

    [Column("download_used")]
    public int DownloadUsed { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("order_item_id")]
    public int OrderItemId { get; set; }

    [Column("download_canceled")]
    public int DownloadCanceled { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Order? Order { get; set; }
}
