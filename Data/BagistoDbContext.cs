using Microsoft.EntityFrameworkCore;
using DOSApi.Models;
using DOSApi.Models.Catalog;
using DOSApi.Models.Customer;
using DOSApi.Models.Sales;
using DOSApi.Models.Cart;
using DOSApi.Models.Cms;
using DOSApi.Models.Rbac;

namespace DOSApi.Data;

public class DOSDbContext : DbContext
{
    public DOSDbContext(DbContextOptions<DOSDbContext> options) : base(options) { }

    // Catalog
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductFlat> ProductFlats => Set<ProductFlat>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductVideo> ProductVideos => Set<ProductVideo>();
    public DbSet<ProductReview> ProductReviews => Set<ProductReview>();
    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();
    public DbSet<ProductInventory> ProductInventories => Set<ProductInventory>();
    public DbSet<ProductPriceIndex> ProductPriceIndices => Set<ProductPriceIndex>();
    public DbSet<ProductCustomerGroupPrice> ProductCustomerGroupPrices => Set<ProductCustomerGroupPrice>();
    public DbSet<Models.Catalog.Attribute> Attributes => Set<Models.Catalog.Attribute>();
    public DbSet<AttributeTranslation> AttributeTranslations => Set<AttributeTranslation>();
    public DbSet<AttributeFamily> AttributeFamilies => Set<AttributeFamily>();
    public DbSet<AttributeGroup> AttributeGroups => Set<AttributeGroup>();
    public DbSet<AttributeOption> AttributeOptions => Set<AttributeOption>();
    public DbSet<AttributeOptionTranslation> AttributeOptionTranslations => Set<AttributeOptionTranslation>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryTranslation> CategoryTranslations => Set<CategoryTranslation>();
    public DbSet<ScrapedBanner> ScrapedBanners => Set<ScrapedBanner>();
    public DbSet<Vendor> Vendors => Set<Vendor>();

    // Customer
    public DbSet<Models.Customer.Customer> Customers => Set<Models.Customer.Customer>();
    public DbSet<CustomerGroup> CustomerGroups => Set<CustomerGroup>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Wishlist> Wishlists => Set<Wishlist>();
    public DbSet<CompareItem> CompareItems => Set<CompareItem>();
    public DbSet<CustomerRefreshToken> CustomerRefreshTokens => Set<CustomerRefreshToken>();
    public DbSet<CustomerAdmin> CustomerAdmins => Set<CustomerAdmin>();
    public DbSet<CustomerDeviceToken> CustomerDeviceTokens => Set<CustomerDeviceToken>();
    public DbSet<GuestDeviceToken> GuestDeviceTokens => Set<GuestDeviceToken>();

    // RBAC
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    // Sales
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentItem> ShipmentItems => Set<ShipmentItem>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundItem> RefundItems => Set<RefundItem>();
    public DbSet<DownloadableLinkPurchased> DownloadableLinkPurchased => Set<DownloadableLinkPurchased>();

    // Cart
    public DbSet<Models.Cart.Cart> Carts => Set<Models.Cart.Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<CartPayment> CartPayments => Set<CartPayment>();
    public DbSet<CartShippingRate> CartShippingRates => Set<CartShippingRate>();

    // CMS
    public DbSet<CmsPage> CmsPages => Set<CmsPage>();
    public DbSet<CmsPageTranslation> CmsPageTranslations => Set<CmsPageTranslation>();

    // Config & Infrastructure
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelTranslation> ChannelTranslations => Set<ChannelTranslation>();
    public DbSet<Locale> Locales => Set<Locale>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<CountryTranslation> CountryTranslations => Set<CountryTranslation>();
    public DbSet<CountryState> CountryStates => Set<CountryState>();
    public DbSet<CountryStateTranslation> CountryStateTranslations => Set<CountryStateTranslation>();
    public DbSet<ThemeCustomization> ThemeCustomizations => Set<ThemeCustomization>();
    public DbSet<ThemeCustomizationTranslation> ThemeCustomizationTranslations => Set<ThemeCustomizationTranslation>();
    public DbSet<StorefrontKey> StorefrontKeys => Set<StorefrontKey>();
    public DbSet<CoreConfig> CoreConfigs => Set<CoreConfig>();
    public DbSet<CartRule> CartRules => Set<CartRule>();
    public DbSet<CartRuleCoupon> CartRuleCoupons => Set<CartRuleCoupon>();
    public DbSet<CatalogRuleProductPrice> CatalogRuleProductPrices => Set<CatalogRuleProductPrice>();
    public DbSet<InventorySource> InventorySources => Set<InventorySource>();

    // Shop API additions
    public DbSet<NewsletterSubscriber> NewsletterSubscribers => Set<NewsletterSubscriber>();
    public DbSet<DeliveryType> DeliveryTypes => Set<DeliveryType>();
    public DbSet<ProductDownloadableLink> ProductDownloadableLinks => Set<ProductDownloadableLink>();
    public DbSet<ProductDownloadableSample> ProductDownloadableSamples => Set<ProductDownloadableSample>();
    public DbSet<ProductBundleOptionProduct> ProductBundleOptionProducts => Set<ProductBundleOptionProduct>();
    public DbSet<ServiceablePincode> ServiceablePincodes => Set<ServiceablePincode>();
    public DbSet<ExtraCharge> ExtraCharges => Set<ExtraCharge>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        // Product self-reference (parent/children)
        mb.Entity<Product>()
            .HasOne(p => p.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(p => p.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        mb.Entity<Product>()
            .HasOne(p => p.AttributeFamily)
            .WithMany()
            .HasForeignKey(p => p.AttributeFamilyId);

        // Product <-> Category M2M
        mb.Entity<Product>()
            .HasMany(p => p.Categories)
            .WithMany(c => c.Products)
            .UsingEntity<Dictionary<string, object>>(
                "product_categories",
                r => r.HasOne<Category>().WithMany().HasForeignKey("category_id"),
                l => l.HasOne<Product>().WithMany().HasForeignKey("product_id")
            );

        // Product <-> Attribute M2M (super_attributes for configurable products)
        mb.Entity<Product>()
            .HasMany(p => p.SuperAttributes)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "product_super_attributes",
                r => r.HasOne<Models.Catalog.Attribute>().WithMany().HasForeignKey("attribute_id"),
                l => l.HasOne<Product>().WithMany().HasForeignKey("product_id")
            );

        // Product related/up-sell/cross-sell M2M
        mb.Entity<Product>()
            .HasMany(p => p.RelatedProducts)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "product_relations",
                r => r.HasOne<Product>().WithMany().HasForeignKey("child_id"),
                l => l.HasOne<Product>().WithMany().HasForeignKey("parent_id")
            );

        mb.Entity<Product>()
            .HasMany(p => p.UpSells)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "product_up_sells",
                r => r.HasOne<Product>().WithMany().HasForeignKey("child_id"),
                l => l.HasOne<Product>().WithMany().HasForeignKey("parent_id")
            );

        mb.Entity<Product>()
            .HasMany(p => p.CrossSells)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "product_cross_sells",
                r => r.HasOne<Product>().WithMany().HasForeignKey("child_id"),
                l => l.HasOne<Product>().WithMany().HasForeignKey("parent_id")
            );

        // ProductFlat
        mb.Entity<ProductFlat>()
            .HasOne(pf => pf.Product)
            .WithMany(p => p.Flats)
            .HasForeignKey(pf => pf.ProductId);

        // ProductImage
        mb.Entity<ProductImage>()
            .HasOne(pi => pi.Product)
            .WithMany(p => p.Images)
            .HasForeignKey(pi => pi.ProductId);

        // ProductVideo
        mb.Entity<ProductVideo>()
            .HasOne(pv => pv.Product)
            .WithMany(p => p.Videos)
            .HasForeignKey(pv => pv.ProductId);

        // ProductReview
        mb.Entity<ProductReview>()
            .HasOne(pr => pr.Product)
            .WithMany(p => p.Reviews)
            .HasForeignKey(pr => pr.ProductId);

        // ProductAttributeValue
        mb.Entity<ProductAttributeValue>()
            .HasOne(pav => pav.Product)
            .WithMany(p => p.AttributeValues)
            .HasForeignKey(pav => pav.ProductId);

        mb.Entity<ProductAttributeValue>()
            .HasOne(pav => pav.Attribute)
            .WithMany()
            .HasForeignKey(pav => pav.AttributeId);

        // ProductInventory
        mb.Entity<ProductInventory>()
            .HasOne(pi => pi.Product)
            .WithMany(p => p.Inventories)
            .HasForeignKey(pi => pi.ProductId);

        // ProductPriceIndex
        mb.Entity<ProductPriceIndex>()
            .HasOne(ppi => ppi.Product)
            .WithMany(p => p.PriceIndices)
            .HasForeignKey(ppi => ppi.ProductId);

        // ProductCustomerGroupPrice
        mb.Entity<ProductCustomerGroupPrice>()
            .HasOne(pcgp => pcgp.Product)
            .WithMany(p => p.CustomerGroupPrices)
            .HasForeignKey(pcgp => pcgp.ProductId);

        // Vendor
        mb.Entity<Vendor>()
            .HasIndex(v => v.Name)
            .IsUnique();

        // Category self-reference
        mb.Entity<Category>()
            .HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        mb.Entity<CategoryTranslation>()
            .HasOne(ct => ct.Category)
            .WithMany(c => c.Translations)
            .HasForeignKey(ct => ct.CategoryId);

        // Category <-> Attribute M2M (filterable)
        mb.Entity<Category>()
            .HasMany(c => c.FilterableAttributes)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "category_filterable_attributes",
                r => r.HasOne<Models.Catalog.Attribute>().WithMany().HasForeignKey("attribute_id"),
                l => l.HasOne<Category>().WithMany().HasForeignKey("category_id")
            );

        // Attribute relations
        mb.Entity<AttributeTranslation>()
            .HasOne(at => at.Attribute)
            .WithMany(a => a.Translations)
            .HasForeignKey(at => at.AttributeId);

        mb.Entity<AttributeOption>()
            .HasOne(ao => ao.Attribute)
            .WithMany(a => a.Options)
            .HasForeignKey(ao => ao.AttributeId);

        mb.Entity<AttributeOptionTranslation>()
            .HasOne(aot => aot.AttributeOption)
            .WithMany(ao => ao.Translations)
            .HasForeignKey(aot => aot.AttributeOptionId);

        mb.Entity<AttributeGroup>()
            .HasOne(ag => ag.AttributeFamily)
            .WithMany(af => af.Groups)
            .HasForeignKey(ag => ag.AttributeFamilyId);

        // AttributeGroup <-> Attribute M2M
        mb.Entity<AttributeGroup>()
            .HasMany(ag => ag.Attributes)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "attribute_group_mappings",
                r => r.HasOne<Models.Catalog.Attribute>().WithMany().HasForeignKey("attribute_id"),
                l => l.HasOne<AttributeGroup>().WithMany().HasForeignKey("attribute_group_id")
            );

        // Customer
        mb.Entity<Models.Customer.Customer>()
            .HasOne(c => c.Group)
            .WithMany()
            .HasForeignKey(c => c.CustomerGroupId);

        mb.Entity<Models.Customer.Customer>()
            .HasOne(c => c.Channel)
            .WithMany()
            .HasForeignKey(c => c.ChannelId);

        // Address
        mb.Entity<Address>()
            .HasOne(a => a.Customer)
            .WithMany(c => c.Addresses)
            .HasForeignKey(a => a.CustomerId);

        // Wishlist
        mb.Entity<Wishlist>()
            .HasOne(w => w.Product)
            .WithMany(p => p.WishlistItems)
            .HasForeignKey(w => w.ProductId);

        mb.Entity<Wishlist>()
            .HasOne(w => w.Customer)
            .WithMany(c => c.WishlistItems)
            .HasForeignKey(w => w.CustomerId);

        // CompareItem
        mb.Entity<CompareItem>()
            .HasOne(ci => ci.Product)
            .WithMany(p => p.CompareItems)
            .HasForeignKey(ci => ci.ProductId);

        mb.Entity<CompareItem>()
            .HasOne(ci => ci.Customer)
            .WithMany(c => c.CompareItems)
            .HasForeignKey(ci => ci.CustomerId);

        // CustomerRefreshToken
        mb.Entity<CustomerRefreshToken>()
            .HasOne(rt => rt.Customer)
            .WithMany()
            .HasForeignKey(rt => rt.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<CustomerRefreshToken>()
            .HasIndex(rt => rt.TokenHash)
            .IsUnique();

        mb.Entity<CustomerRefreshToken>()
            .HasIndex(rt => rt.CustomerId);

        // CustomerAdmin
        mb.Entity<CustomerAdmin>()
            .HasOne(a => a.Customer)
            .WithMany()
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<CustomerAdmin>()
            .HasIndex(a => a.CustomerId)
            .IsUnique();

        // Order
        mb.Entity<Order>()
            .HasOne(o => o.Customer)
            .WithMany(c => c.Orders)
            .HasForeignKey(o => o.CustomerId);

        // Order doesn't have a navigation to Addresses - we query via Address.OrderId
        mb.Entity<Order>().Ignore(o => o.Addresses);

        mb.Entity<OrderItem>()
            .HasOne(oi => oi.Order)
            .WithMany(o => o.Items)
            .HasForeignKey(oi => oi.OrderId);

        mb.Entity<OrderItem>()
            .HasOne(oi => oi.ParentItem)
            .WithMany(oi => oi.ChildItems)
            .HasForeignKey(oi => oi.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Order ↔ OrderPayment is 1:1 (Order.Payment is a single OrderPayment?,
        // not a collection). Same shadow-FK bug as Cart ↔ CartPayment — using
        // .WithMany() here caused EF to invent an `OrderId1` column. Must be
        // a true 1:1 with the FK on the dependent side.
        mb.Entity<OrderPayment>()
            .HasOne(op => op.Order)
            .WithOne(o => o.Payment)
            .HasForeignKey<OrderPayment>(op => op.OrderId);

        mb.Entity<Invoice>()
            .HasOne(i => i.Order)
            .WithMany(o => o.Invoices)
            .HasForeignKey(i => i.OrderId);

        mb.Entity<InvoiceItem>()
            .HasOne(ii => ii.Invoice)
            .WithMany(i => i.Items)
            .HasForeignKey(ii => ii.InvoiceId);

        mb.Entity<Shipment>()
            .HasOne(s => s.Order)
            .WithMany(o => o.Shipments)
            .HasForeignKey(s => s.OrderId);

        mb.Entity<ShipmentItem>()
            .HasOne(si => si.Shipment)
            .WithMany(s => s.Items)
            .HasForeignKey(si => si.ShipmentId);

        mb.Entity<Refund>()
            .HasOne(r => r.Order)
            .WithMany(o => o.Refunds)
            .HasForeignKey(r => r.OrderId);

        mb.Entity<RefundItem>()
            .HasOne(ri => ri.Refund)
            .WithMany(r => r.Items)
            .HasForeignKey(ri => ri.RefundId);

        // Cart
        mb.Entity<Models.Cart.Cart>()
            .HasOne(c => c.Customer)
            .WithMany()
            .HasForeignKey(c => c.CustomerId);

        mb.Entity<CartItem>()
            .HasOne(ci => ci.Cart)
            .WithMany(c => c.Items)
            .HasForeignKey(ci => ci.CartId);

        mb.Entity<CartItem>()
            .HasOne(ci => ci.Product)
            .WithMany()
            .HasForeignKey(ci => ci.ProductId);

        mb.Entity<CartItem>()
            .HasOne(ci => ci.ParentItem)
            .WithMany(ci => ci.ChildItems)
            .HasForeignKey(ci => ci.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cart ↔ CartPayment is 1:1 — Cart.Payment is a single CartPayment?,
        // not a collection. Using .WithMany() here made EF generate a shadow
        // FK column `CartId1` on cart_payment because it couldn't pair the
        // singular Cart.Payment navigation with this relationship, producing
        // `Unknown column 'c.CartId1' in 'field list'` at query time. Must be
        // configured as a true 1:1 with the FK on the dependent side.
        mb.Entity<CartPayment>()
            .HasOne(cp => cp.Cart)
            .WithOne(c => c.Payment)
            .HasForeignKey<CartPayment>(cp => cp.CartId);

        mb.Entity<CartShippingRate>()
            .HasOne(csr => csr.Cart)
            .WithMany(c => c.ShippingRates)
            .HasForeignKey(csr => csr.CartId);

        // CMS
        mb.Entity<CmsPageTranslation>()
            .HasOne(cpt => cpt.CmsPage)
            .WithMany(cp => cp.Translations)
            .HasForeignKey(cpt => cpt.CmsPageId);

        // Channel
        mb.Entity<Channel>()
            .HasOne(c => c.DefaultLocale)
            .WithMany()
            .HasForeignKey(c => c.DefaultLocaleId);

        mb.Entity<Channel>()
            .HasOne(c => c.BaseCurrency)
            .WithMany()
            .HasForeignKey(c => c.BaseCurrencyId);

        mb.Entity<ChannelTranslation>()
            .HasOne(ct => ct.Channel)
            .WithMany(c => c.Translations)
            .HasForeignKey(ct => ct.ChannelId);

        mb.Entity<Channel>()
            .HasMany(c => c.Locales)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "channel_locales",
                r => r.HasOne<Locale>().WithMany().HasForeignKey("locale_id"),
                l => l.HasOne<Channel>().WithMany().HasForeignKey("channel_id")
            );

        mb.Entity<Channel>()
            .HasMany(c => c.Currencies)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "channel_currencies",
                r => r.HasOne<Currency>().WithMany().HasForeignKey("currency_id"),
                l => l.HasOne<Channel>().WithMany().HasForeignKey("channel_id")
            );

        // Country
        mb.Entity<CountryTranslation>()
            .HasOne(ct => ct.Country)
            .WithMany(c => c.Translations)
            .HasForeignKey(ct => ct.CountryId);

        mb.Entity<CountryState>()
            .HasOne(cs => cs.Country)
            .WithMany(c => c.States)
            .HasForeignKey(cs => cs.CountryId);

        mb.Entity<CountryStateTranslation>()
            .HasOne(cst => cst.CountryState)
            .WithMany(cs => cs.Translations)
            .HasForeignKey(cst => cst.CountryStateId);

        // Theme
        mb.Entity<ThemeCustomizationTranslation>()
            .HasOne(t => t.ThemeCustomization)
            .WithMany(tc => tc.Translations)
            .HasForeignKey(t => t.ThemeCustomizationId);

        // CartRule
        mb.Entity<CartRuleCoupon>()
            .HasOne(crc => crc.CartRule)
            .WithMany(cr => cr.Coupons)
            .HasForeignKey(crc => crc.CartRuleId);
    }
}
