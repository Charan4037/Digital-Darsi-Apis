using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BagistoApi.Models.Customer;

[Table("customers")]
public class Customer
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("first_name")]
    public string FirstName { get; set; } = "";

    [Column("last_name")]
    public string LastName { get; set; } = "";

    [Column("gender")]
    public string? Gender { get; set; }

    [Column("date_of_birth")]
    public DateTime? DateOfBirth { get; set; }

    [Column("email")]
    public string? Email { get; set; }

    [Column("phone")]
    public string? Phone { get; set; }

    [Column("image")]
    public string? Image { get; set; }

    [Column("status")]
    public int Status { get; set; } = 1;

    [Column("password")]
    public string? Password { get; set; }

    [Column("api_token")]
    public string? ApiToken { get; set; }

    [Column("customer_group_id")]
    public int? CustomerGroupId { get; set; }

    [Column("channel_id")]
    public int? ChannelId { get; set; }

    [Column("subscribed_to_news_letter")]
    public bool SubscribedToNewsLetter { get; set; }

    [Column("is_verified")]
    public bool IsVerified { get; set; }

    [Column("is_suspended")]
    public int IsSuspended { get; set; }

    [Column("token")]
    public string? Token { get; set; }

    [Column("remember_token")]
    public string? RememberToken { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public CustomerGroup? Group { get; set; }
    public Models.Channel? Channel { get; set; }
    public List<Address> Addresses { get; set; } = new();
    public List<Sales.Order> Orders { get; set; } = new();
    public List<Catalog.ProductReview> Reviews { get; set; } = new();
    public List<Wishlist> WishlistItems { get; set; } = new();
    public List<CompareItem> CompareItems { get; set; } = new();
}

[Table("customer_groups")]
public class CustomerGroup
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("is_user_defined")]
    public bool IsUserDefined { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

[Table("addresses")]
public class Address
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("address_type")]
    public string AddressType { get; set; } = "customer_address";

    [Column("parent_address_id")]
    public int? ParentAddressId { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("cart_id")]
    public int? CartId { get; set; }

    [Column("order_id")]
    public int? OrderId { get; set; }

    [Column("first_name")]
    public string FirstName { get; set; } = "";

    [Column("last_name")]
    public string LastName { get; set; } = "";

    [Column("gender")]
    public string? Gender { get; set; }

    [Column("company_name")]
    public string? CompanyName { get; set; }

    [Column("address")]
    public string AddressLine { get; set; } = "";

    [Column("city")]
    public string City { get; set; } = "";

    [Column("state")]
    public string? State { get; set; }

    [Column("country")]
    public string? Country { get; set; }

    [Column("postcode")]
    public string? Postcode { get; set; }

    [Column("email")]
    public string? Email { get; set; }

    [Column("phone")]
    public string? Phone { get; set; }

    [Column("vat_id")]
    public string? VatId { get; set; }

    [Column("default_address")]
    public bool DefaultAddress { get; set; }

    [Column("use_for_shipping")]
    public bool UseForShipping { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Customer? Customer { get; set; }
}

[Table("wishlist_items")]
public class Wishlist
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("channel_id")]
    public int ChannelId { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("moved_to_cart")]
    public DateTime? MovedToCart { get; set; }

    [Column("shared")]
    public bool? Shared { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Catalog.Product? Product { get; set; }
    public Customer? Customer { get; set; }
    public Models.Channel? Channel { get; set; }
}

[Table("compare_items")]
public class CompareItem
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Catalog.Product? Product { get; set; }
    public Customer? Customer { get; set; }
}
