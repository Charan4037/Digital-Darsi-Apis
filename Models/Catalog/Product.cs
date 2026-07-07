using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DOSApi.Models.Customer;
using DOSApi.Models.Sales;
using DOSApi.Models.Cart;

namespace DOSApi.Models.Catalog;

[Table("products")]
public class Product
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("sku")]
    public string Sku { get; set; } = "";

    [Column("type")]
    public string Type { get; set; } = "simple";

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("attribute_family_id")]
    public int? AttributeFamilyId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Product? Parent { get; set; }
    public List<Product> Children { get; set; } = new();
    public AttributeFamily? AttributeFamily { get; set; }
    public List<ProductFlat> Flats { get; set; } = new();
    public List<ProductImage> Images { get; set; } = new();
    public List<ProductVideo> Videos { get; set; } = new();
    public List<ProductReview> Reviews { get; set; } = new();
    public List<ProductAttributeValue> AttributeValues { get; set; } = new();
    public List<ProductInventory> Inventories { get; set; } = new();
    public List<ProductPriceIndex> PriceIndices { get; set; } = new();
    public List<ProductCustomerGroupPrice> CustomerGroupPrices { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public List<Attribute> SuperAttributes { get; set; } = new();
    public List<Product> RelatedProducts { get; set; } = new();
    public List<Product> UpSells { get; set; } = new();
    public List<Product> CrossSells { get; set; } = new();
    public List<Wishlist> WishlistItems { get; set; } = new();
    public List<CompareItem> CompareItems { get; set; } = new();
}

[Table("product_flat")]
public class ProductFlat
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("sku")]
    public string Sku { get; set; } = "";

    [Column("type")]
    public string? Type { get; set; }

    [Column("product_number")]
    public string? ProductNumber { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("short_description")]
    public string? ShortDescription { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("url_key")]
    public string? UrlKey { get; set; }

    [Column("new")]
    public bool? New { get; set; }

    [Column("featured")]
    public bool? Featured { get; set; }

    [Column("status")]
    public bool? Status { get; set; }

    [Column("meta_title")]
    public string? MetaTitle { get; set; }

    [Column("meta_keywords")]
    public string? MetaKeywords { get; set; }

    [Column("meta_description")]
    public string? MetaDescription { get; set; }

    [Column("price")]
    public decimal? Price { get; set; }

    [Column("special_price")]
    public decimal? SpecialPrice { get; set; }

    [Column("special_price_from")]
    public DateTime? SpecialPriceFrom { get; set; }

    [Column("special_price_to")]
    public DateTime? SpecialPriceTo { get; set; }

    [Column("weight")]
    public decimal? Weight { get; set; }

    [Column("locale")]
    public string? Locale { get; set; }

    [Column("channel")]
    public string? Channel { get; set; }

    [Column("attribute_family_id")]
    public int? AttributeFamilyId { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("visible_individually")]
    public bool? VisibleIndividually { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Product? Product { get; set; }
}

[Table("product_images")]
public class ProductImage
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("type")]
    public string? Type { get; set; }

    [Column("path")]
    public string Path { get; set; } = "";

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("position")]
    public int Position { get; set; }

    public Product? Product { get; set; }
}

[Table("product_videos")]
public class ProductVideo
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("type")]
    public string? Type { get; set; }

    [Column("path")]
    public string Path { get; set; } = "";

    [Column("position")]
    public int Position { get; set; }

    public Product? Product { get; set; }
}

[Table("product_reviews")]
public class ProductReview
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("rating")]
    public int Rating { get; set; }

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Product? Product { get; set; }
    public Models.Customer.Customer? Customer { get; set; }
}

[Table("product_attribute_values")]
public class ProductAttributeValue
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("locale")]
    public string? Locale { get; set; }

    [Column("channel")]
    public string? Channel { get; set; }

    [Column("text_value")]
    public string? TextValue { get; set; }

    [Column("boolean_value")]
    public bool? BooleanValue { get; set; }

    [Column("integer_value")]
    public int? IntegerValue { get; set; }

    [Column("float_value")]
    public decimal? FloatValue { get; set; }

    [Column("datetime_value")]
    public DateTime? DatetimeValue { get; set; }

    [Column("date_value")]
    public DateTime? DateValue { get; set; }

    [Column("json_value")]
    public string? JsonValue { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("attribute_id")]
    public int AttributeId { get; set; }

    [Column("unique_id")]
    public string? UniqueId { get; set; }

    public Product? Product { get; set; }
    public Attribute? Attribute { get; set; }
}

[Table("product_inventories")]
public class ProductInventory
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("qty")]
    public int Qty { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("vendor_id")]
    public int VendorId { get; set; }

    [Column("inventory_source_id")]
    public int InventorySourceId { get; set; }

    public Product? Product { get; set; }
}

[Table("product_price_indices")]
public class ProductPriceIndex
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("customer_group_id")]
    public int? CustomerGroupId { get; set; }

    [Column("min_price")]
    public decimal MinPrice { get; set; }

    [Column("regular_min_price")]
    public decimal RegularMinPrice { get; set; }

    [Column("max_price")]
    public decimal MaxPrice { get; set; }

    [Column("regular_max_price")]
    public decimal RegularMaxPrice { get; set; }

    [Column("channel_id")]
    public int? ChannelId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Product? Product { get; set; }
}

[Table("product_customer_group_prices")]
public class ProductCustomerGroupPrice
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Column("qty")]
    public int Qty { get; set; }

    [Column("value_type")]
    public string ValueType { get; set; } = "fixed";

    [Column("value")]
    public decimal Value { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("customer_group_id")]
    public int? CustomerGroupId { get; set; }

    [Column("unique_id")]
    public string? UniqueId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Product? Product { get; set; }
}
