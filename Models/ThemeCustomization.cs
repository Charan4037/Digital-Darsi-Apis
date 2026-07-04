using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models;

[Table("theme_customizations")]
public class ThemeCustomization
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("theme_code")]
    public string? ThemeCode { get; set; }

    [Column("type")]
    public string Type { get; set; } = "";

    [Column("name")]
    public string? Name { get; set; }

    [Column("status")]
    public bool Status { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("channel_id")]
    public int? ChannelId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public List<ThemeCustomizationTranslation> Translations { get; set; } = new();
}

[Table("theme_customization_translations")]
public class ThemeCustomizationTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("theme_customization_id")]
    public int ThemeCustomizationId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("options")]
    public string? Options { get; set; }

    public ThemeCustomization? ThemeCustomization { get; set; }
}

[Table("storefront_keys")]
public class StorefrontKey
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("key")]
    public string Key { get; set; } = "";

    [Column("key_type")]
    public string KeyType { get; set; } = "shop";

    [Column("name")]
    public string? Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("rate_limit")]
    public int? RateLimit { get; set; }

    [Column("allowed_ips")]
    public string? AllowedIps { get; set; }

    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

[Table("core_config")]
public class CoreConfig
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("value")]
    public string? Value { get; set; }

    [Column("channel_code")]
    public string? ChannelCode { get; set; }

    [Column("locale_code")]
    public string? LocaleCode { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

[Table("cart_rules")]
public class CartRule
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("starts_from")]
    public DateTime? StartsFrom { get; set; }

    [Column("ends_till")]
    public DateTime? EndsTill { get; set; }

    [Column("status")]
    public bool Status { get; set; }

    [Column("coupon_type")]
    public int CouponType { get; set; } = 1;

    [Column("use_auto_generation")]
    public bool UseAutoGeneration { get; set; }

    [Column("usage_per_customer")]
    public int UsagePerCustomer { get; set; }

    [Column("uses_per_coupon")]
    public int UsesPerCoupon { get; set; }

    [Column("times_used")]
    public int TimesUsed { get; set; }

    [Column("condition_type")]
    public bool ConditionType { get; set; } = true;

    [Column("conditions")]
    public string? Conditions { get; set; }

    [Column("end_other_rules")]
    public bool EndOtherRules { get; set; }

    [Column("uses_attribute_conditions")]
    public bool UsesAttributeConditions { get; set; }

    [Column("action_type")]
    public string? ActionType { get; set; }

    [Column("discount_amount")]
    public decimal DiscountAmount { get; set; }

    [Column("discount_quantity")]
    public int DiscountQuantity { get; set; } = 1;

    [Column("discount_step")]
    public string DiscountStep { get; set; } = "1";

    [Column("apply_to_shipping")]
    public bool ApplyToShipping { get; set; }

    [Column("free_shipping")]
    public bool FreeShipping { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public List<CartRuleCoupon> Coupons { get; set; } = new();
}

[Table("cart_rule_coupons")]
public class CartRuleCoupon
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string? Code { get; set; }

    [Column("usage_limit")]
    public int UsageLimit { get; set; }

    [Column("usage_per_customer")]
    public int UsagePerCustomer { get; set; }

    [Column("times_used")]
    public int TimesUsed { get; set; }

    [Column("type")]
    public int Type { get; set; }

    [Column("is_primary")]
    public bool IsPrimary { get; set; }

    [Column("expired_at")]
    public DateTime? ExpiredAt { get; set; }

    [Column("cart_rule_id")]
    public int CartRuleId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public CartRule? CartRule { get; set; }
}

[Table("catalog_rule_product_prices")]
public class CatalogRuleProductPrice
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("customer_group_id")]
    public int CustomerGroupId { get; set; }

    [Column("catalog_rule_id")]
    public int CatalogRuleId { get; set; }

    [Column("channel_id")]
    public int ChannelId { get; set; }

    [Column("price")]
    public decimal Price { get; set; }

    [Column("end_date")]
    public DateTime? EndDate { get; set; }
}

[Table("inventory_sources")]
public class InventorySource
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("contact_name")]
    public string ContactName { get; set; } = "";

    [Column("contact_email")]
    public string ContactEmail { get; set; } = "";

    [Column("contact_number")]
    public string ContactNumber { get; set; } = "";

    [Column("contact_fax")]
    public string? ContactFax { get; set; }

    [Column("country")]
    public string Country { get; set; } = "";

    [Column("state")]
    public string State { get; set; } = "";

    [Column("city")]
    public string City { get; set; } = "";

    [Column("street")]
    public string Street { get; set; } = "";

    [Column("postcode")]
    public string Postcode { get; set; } = "";

    [Column("priority")]
    public int Priority { get; set; }

    [Column("latitude")]
    public decimal? Latitude { get; set; }

    [Column("longitude")]
    public decimal? Longitude { get; set; }

    [Column("status")]
    public bool StatusFlag { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
