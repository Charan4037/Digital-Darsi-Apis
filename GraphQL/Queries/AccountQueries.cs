using HotChocolate;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.GraphQL.Types;
using DOSApi.Models.Catalog;
using DOSApi.Models.Sales;
using DOSApi.Services;

namespace DOSApi.GraphQL.Queries;

[ExtendObjectType("Query")]
public class AccountQueries
{
    public async Task<CustomerProfileResult?> GetReadCustomerProfile([Service] AuthService auth)
    {
        var c = await auth.GetCurrentCustomerAsync();
        if (c == null) return null;
        return new CustomerProfileResult
        {
            Id = c.Id, FirstName = c.FirstName, LastName = c.LastName, Email = c.Email,
            DateOfBirth = c.DateOfBirth?.ToString("yyyy-MM-dd"), Gender = c.Gender,
            Phone = c.Phone, Status = c.Status == 1,
            SubscribedToNewsLetter = c.SubscribedToNewsLetter,
            IsVerified = c.IsVerified, Image = c.Image
        };
    }

    public async Task<Connection<AddressResult>> GetGetCustomerAddresses(
        [Service] AccountService svc, [Service] AuthService auth,
        int? first = 20, string? after = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<AddressResult>();

        var q = svc.GetAddresses(cid.Value);
        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(a => MapAddress(a)).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<Connection<ProductReviewResult>> GetProductReviews(
        [Service] DOSDbContext db, int? first = 20, string? after = null,
        int? productId = null, int? product_id = null)
    {
        var pid = productId ?? product_id;
        var q = db.ProductReviews.Where(r => r.Status == "approved").AsQueryable();
        if (pid.HasValue) q = q.Where(r => r.ProductId == pid);

        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.OrderByDescending(r => r.CreatedAt).Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(r => new ProductReviewResult
        {
            Id = r.Id, _Id = r.Id, Name = r.Name, Title = r.Title, Rating = r.Rating,
            Comment = r.Comment, Status = r.Status,
            CreatedAt = r.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = r.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<Connection<CustomerReviewResult>> GetCustomerReviews(
        [Service] DOSDbContext db, [Service] AuthService auth, [Service] IConfiguration config,
        int? first = 20, string? after = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<CustomerReviewResult>();
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        var q = db.ProductReviews.Include(r => r.Product).ThenInclude(p => p!.Images)
            .Include(r => r.Product).ThenInclude(p => p!.Flats)
            .Where(r => r.CustomerId == cid);

        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.OrderByDescending(r => r.CreatedAt).Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(r => new CustomerReviewResult
        {
            Id = r.Id, _Id = r.Id, Title = r.Title, Comment = r.Comment, Rating = r.Rating,
            Status = r.Status, Name = r.Name,
            CreatedAt = r.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = r.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            Product = r.Product != null ? new ReviewProductResult
            {
                Id = $"/api/shop/products/{r.Product.Id}", _Id = r.Product.Id, Sku = r.Product.Sku,
                Type = r.Product.Type, Name = r.Product.Flats.FirstOrDefault()?.Name ?? r.Product.Sku,
                BaseImageUrl = r.Product.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path is { } p ? $"{baseUrl}/storage/{p}" : null,
            } : null,
            Customer = new ReviewCustomerResult { Id = cid.Value, _Id = cid.Value }
        }).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<Connection<WishlistResult>> GetWishlists(
        [Service] DOSDbContext db, [Service] AuthService auth, [Service] IConfiguration config,
        int? first = 20, string? after = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<WishlistResult>();
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        var q = db.Wishlists.Include(w => w.Product).ThenInclude(p => p!.Flats)
            .Include(w => w.Channel).ThenInclude(c => c!.Translations)
            .Where(w => w.CustomerId == cid);

        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.OrderByDescending(w => w.Id).Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(w =>
        {
            var flat = w.Product?.Flats.FirstOrDefault();
            return new WishlistResult
            {
                Id = $"/api/shop/wishlists/{w.Id}", _Id = w.Id,
                Product = w.Product != null ? new WishlistProductResult
                {
                    Id = $"/api/shop/products/{w.Product.Id}", _Id = w.Product.Id,
                    Name = flat?.Name, Price = flat?.Price ?? 0, Sku = w.Product.Sku,
                    Type = w.Product.Type, Description = flat?.Description,
                    BaseImageUrl = flat?.UrlKey, UrlKey = flat?.UrlKey
                } : null,
                Customer = new ReviewCustomerResult { Id = cid.Value, _Id = cid.Value },
                Channel = w.Channel != null ? new WishlistChannelResult
                {
                    Id = w.Channel.Id, Code = w.Channel.Code,
                    Translation = w.Channel.Translations.FirstOrDefault() is { } t
                        ? new TranslationResult { Id = t.Id, Name = t.Name } : null
                } : null,
                CreatedAt = w.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                UpdatedAt = w.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<Connection<OrderResult>> GetCustomerOrders(
        [Service] AccountService svc, [Service] AuthService auth,
        int? first = 20, string? after = null, string? status = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<OrderResult>();

        var q = svc.GetOrders(cid.Value, status);
        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(o => MapOrder(o)).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<OrderDetailResult?> GetCustomerOrder(
        [Service] AccountService svc, [Service] AuthService auth, string id)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return null;

        var numId = ParseIriId(id);
        var order = await svc.GetOrderDetailAsync(cid.Value, numId);
        if (order == null) return null;

        var addresses = await svc.GetOrderAddressesAsync(order.Id);

        return new OrderDetailResult
        {
            IncrementId = order.IncrementId, Status = order.Status, ChannelName = order.ChannelName,
            CustomerEmail = order.CustomerEmail, CustomerFirstName = order.CustomerFirstName,
            CustomerLastName = order.CustomerLastName, ShippingMethod = order.ShippingMethod,
            ShippingTitle = order.ShippingTitle, CouponCode = order.CouponCode,
            TotalItemCount = order.TotalItemCount, TotalQtyOrdered = order.TotalQtyOrdered,
            GrandTotal = order.GrandTotal, BaseGrandTotal = order.BaseGrandTotal,
            GrandTotalInvoiced = order.GrandTotalInvoiced, GrandTotalRefunded = order.GrandTotalRefunded,
            SubTotal = order.SubTotal, BaseSubTotal = order.BaseSubTotal,
            TaxAmount = order.TaxAmount, BaseTaxAmount = order.BaseTaxAmount,
            DiscountAmount = order.DiscountAmount, BaseDiscountAmount = order.BaseDiscountAmount,
            ShippingAmount = order.ShippingAmount, BaseShippingAmount = order.BaseShippingAmount,
            BaseCurrencyCode = order.BaseCurrencyCode, ChannelCurrencyCode = order.ChannelCurrencyCode,
            OrderCurrencyCode = order.OrderCurrencyCode,
            Payment = order.Payment != null ? new OrderPaymentResult { Id = order.Payment.Id, MethodTitle = order.Payment.MethodTitle } : null,
            Items = ConnectionHelper.ToConnection(order.Items.Select(i => new OrderItemResult
            {
                Id = i.Id, Sku = i.Sku, Name = i.Name,
                QtyOrdered = i.QtyOrdered, QtyShipped = i.QtyShipped,
                QtyInvoiced = i.QtyInvoiced, QtyCanceled = i.QtyCanceled, QtyRefunded = i.QtyRefunded
            }).ToList(), order.Items.Count, 0, order.Items.Count),
            Addresses = ConnectionHelper.ToConnection(addresses.Select(a => MapAddress(a)).ToList(), addresses.Count, 0, addresses.Count),
            CreatedAt = order.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = order.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    public async Task<Connection<InvoiceResult>> GetCustomerInvoices(
        [Service] AccountService svc, [Service] AuthService auth,
        int? first = 20, string? after = null, int? orderId = null, string? state = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<InvoiceResult>();

        var q = svc.GetInvoices(cid.Value, orderId, state);
        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.Include(i => i.Items).Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(i => MapInvoice(i)).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<InvoiceResult?> GetCustomerInvoice(
        [Service] AccountService svc, [Service] AuthService auth, string id)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return null;
        var inv = await svc.GetInvoiceDetailAsync(cid.Value, ParseIriId(id));
        return inv != null ? MapInvoice(inv) : null;
    }

    public async Task<Connection<ShipmentResult>> GetCustomerOrderShipments(
        [Service] AccountService svc, [Service] AuthService auth, int orderId)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<ShipmentResult>();

        var items = await svc.GetShipments(orderId, cid.Value).ToListAsync();
        var results = items.Select(s => MapShipment(s)).ToList();
        return ConnectionHelper.ToConnection(results, results.Count, 0, results.Count);
    }

    public async Task<ShipmentResult?> GetCustomerOrderShipment(
        [Service] AccountService svc, [Service] AuthService auth, int id)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return null;
        var s = await svc.GetShipmentDetailAsync(cid.Value, id);
        return s != null ? MapShipment(s) : null;
    }

    public async Task<Connection<LocaleResult>> GetLocales([Service] DOSDbContext db)
    {
        var locales = await db.Locales.ToListAsync();
        var results = locales.Select(l => new LocaleResult
        {
            Id = $"/api/shop/locales/{l.Id}", _Id = l.Id, Code = l.Code, Name = l.Name, Direction = l.Direction
        }).ToList();
        return ConnectionHelper.ToConnection(results, results.Count, 0, results.Count);
    }

    public async Task<Connection<DownloadableResult>> GetCustomerDownloadableProducts(
        [Service] DOSDbContext db, [Service] AuthService auth,
        int? first = 20, string? after = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<DownloadableResult>();

        var q = db.DownloadableLinkPurchased.Include(d => d.Order).Where(d => d.CustomerId == cid);
        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.OrderByDescending(d => d.Id).Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(d => new DownloadableResult
        {
            _Id = d.Id, ProductName = d.ProductName, Name = d.Name, FileName = d.FileName,
            Type = d.Type, DownloadBought = d.DownloadBought, DownloadUsed = d.DownloadUsed,
            DownloadCanceled = d.DownloadCanceled, Status = d.Status,
            RemainingDownloads = d.DownloadBought - d.DownloadUsed,
            Order = d.Order != null ? new DownloadableOrderResult { _Id = d.Order.Id, IncrementId = d.Order.IncrementId, Status = d.Order.Status } : null,
            CreatedAt = d.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = d.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    public async Task<Connection<CmsPageResult>> GetPages([Service] DOSDbContext db, [Service] IConfiguration config)
    {
        var locale = config["App:Locale"] ?? "en";
        var pages = await db.CmsPages.Include(p => p.Translations).ToListAsync();
        var results = pages.Select(p =>
        {
            var t = p.Translations.FirstOrDefault(tr => tr.Locale == locale) ?? p.Translations.FirstOrDefault();
            return new CmsPageResult
            {
                Id = $"/api/shop/cms-pages/{p.Id}", _Id = p.Id, Layout = p.Layout,
                CreatedAt = p.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                UpdatedAt = p.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                Translation = t != null ? new CmsTranslationResult
                {
                    Id = t.Id, _Id = t.Id, PageTitle = t.PageTitle, UrlKey = t.UrlKey,
                    HtmlContent = t.HtmlContent, MetaTitle = t.MetaTitle,
                    MetaDescription = t.MetaDescription, MetaKeywords = t.MetaKeywords, Locale = t.Locale
                } : null
            };
        }).ToList();
        return ConnectionHelper.ToConnection(results, results.Count, 0, results.Count);
    }

    public async Task<Connection<CompareItemResult>> GetCompareItems(
        [Service] DOSDbContext db, [Service] AuthService auth, [Service] IConfiguration config,
        int? first = 20, string? after = null)
    {
        var cid = auth.GetCurrentCustomerId();
        if (cid == null) return new Connection<CompareItemResult>();
        var baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";

        var q = db.CompareItems.Include(ci => ci.Product).ThenInclude(p => p!.Flats)
            .Include(ci => ci.Product).ThenInclude(p => p!.Images)
            .Include(ci => ci.Customer)
            .Where(ci => ci.CustomerId == cid);

        var total = await q.CountAsync();
        var offset = ConnectionHelper.DecodeCursor(after);
        var items = await q.OrderByDescending(ci => ci.Id).Skip(offset).Take(first ?? 20).ToListAsync();

        var results = items.Select(ci =>
        {
            var flat = ci.Product?.Flats.FirstOrDefault();
            var img = ci.Product?.Images.OrderBy(i => i.Position).FirstOrDefault()?.Path;
            return new CompareItemResult
            {
                Id = $"/api/shop/compare-items/{ci.Id}", _Id = ci.Id,
                CreatedAt = ci.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                UpdatedAt = ci.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                Product = ci.Product != null ? new CompareProductResult
                {
                    Id = ci.Product.Id, Name = flat?.Name, Description = flat?.Description,
                    Price = flat?.Price ?? 0, BaseImageUrl = img != null ? $"{baseUrl}/storage/{img}" : null,
                    UrlKey = flat?.UrlKey
                } : null,
                Customer = ci.Customer != null ? new CompareCustomerResult
                {
                    Id = ci.Customer.Id, Email = ci.Customer.Email,
                    FirstName = ci.Customer.FirstName, LastName = ci.Customer.LastName
                } : null
            };
        }).ToList();
        return ConnectionHelper.ToConnection(results, total, offset, first ?? 20);
    }

    // ─── Helpers ────────────────────────────────────────────
    private static int ParseIriId(string id)
    {
        if (id.Contains('/')) { var parts = id.Split('/'); int.TryParse(parts.Last(), out var n); return n; }
        int.TryParse(id, out var num); return num;
    }

    private static AddressResult MapAddress(Models.Customer.Address a) => new()
    {
        Id = $"/api/shop/addresses/{a.Id}", _Id = a.Id, AddressType = a.AddressType,
        FirstName = a.FirstName, LastName = a.LastName, Email = a.Email,
        CompanyName = a.CompanyName, VatId = a.VatId, Address = a.AddressLine,
        City = a.City, State = a.State, Country = a.Country, Postcode = a.Postcode,
        Phone = a.Phone, DefaultAddress = a.DefaultAddress, UseForShipping = a.UseForShipping,
        CreatedAt = a.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedAt = a.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        Name = $"{a.FirstName} {a.LastName}"
    };

    private static OrderResult MapOrder(Order o) => new()
    {
        Id = $"/api/shop/customer-orders/{o.Id}", _Id = o.Id, IncrementId = o.IncrementId,
        Status = o.Status, ChannelName = o.ChannelName, CustomerEmail = o.CustomerEmail,
        CustomerFirstName = o.CustomerFirstName, CustomerLastName = o.CustomerLastName,
        TotalItemCount = o.TotalItemCount, TotalQtyOrdered = o.TotalQtyOrdered,
        GrandTotal = o.GrandTotal, BaseGrandTotal = o.BaseGrandTotal,
        SubTotal = o.SubTotal, TaxAmount = o.TaxAmount, DiscountAmount = o.DiscountAmount,
        ShippingAmount = o.ShippingAmount, ShippingTitle = o.ShippingTitle,
        CouponCode = o.CouponCode, OrderCurrencyCode = o.OrderCurrencyCode,
        BaseCurrencyCode = o.BaseCurrencyCode,
        CreatedAt = o.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedAt = o.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
    };

    private static InvoiceResult MapInvoice(Invoice i) => new()
    {
        _Id = i.Id, IncrementId = i.IncrementId, State = i.State, TotalQty = i.TotalQty,
        OrderCurrencyCode = i.OrderCurrencyCode, GrandTotal = i.GrandTotal,
        BaseGrandTotal = i.BaseGrandTotal, SubTotal = i.SubTotal, BaseSubTotal = i.BaseSubTotal,
        ShippingAmount = i.ShippingAmount, BaseShippingAmount = i.BaseShippingAmount,
        TaxAmount = i.TaxAmount, BaseTaxAmount = i.BaseTaxAmount,
        DiscountAmount = i.DiscountAmount, BaseDiscountAmount = i.BaseDiscountAmount,
        BaseCurrencyCode = i.BaseCurrencyCode, TransactionId = i.TransactionId,
        CreatedAt = i.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedAt = i.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        Items = ConnectionHelper.ToConnection(i.Items.Select(ii => new InvoiceItemResult
        {
            Id = ii.Id, _Id = ii.Id, Sku = ii.Sku, Name = ii.Name, Qty = ii.Qty,
            Price = ii.Price, BasePrice = ii.BasePrice, Total = ii.Total, BaseTotal = ii.BaseTotal,
            TaxAmount = ii.TaxAmount, BaseTaxAmount = ii.BaseTaxAmount,
            DiscountPercent = ii.DiscountPercent, DiscountAmount = ii.DiscountAmount,
            ProductId = ii.ProductId, ProductType = ii.ProductType,
            OrderItemId = ii.OrderItemId, InvoiceId = ii.InvoiceId,
            CreatedAt = ii.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = ii.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList(), i.Items.Count, 0, i.Items.Count)
    };

    private static ShipmentResult MapShipment(Shipment s) => new()
    {
        Id = s.Id, _Id = s.Id, Status = s.Status, TrackNumber = s.TrackNumber,
        CarrierTitle = s.CarrierTitle, TotalQty = s.TotalQty,
        CreatedAt = s.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
        Items = ConnectionHelper.ToConnection(s.Items.Select(si => new ShipmentItemResult
        {
            Id = si.Id, Name = si.Name, Sku = si.Sku, Qty = si.Qty
        }).ToList(), s.Items.Count, 0, s.Items.Count)
    };
}

// ─── Result DTOs ────────────────────────────────────────────

public class CustomerProfileResult
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Phone { get; set; }
    public bool Status { get; set; }
    public bool SubscribedToNewsLetter { get; set; }
    public bool IsVerified { get; set; }
    public string? Image { get; set; }
}

public class AddressResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string AddressType { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? CompanyName { get; set; }
    public string? VatId { get; set; }
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? Postcode { get; set; }
    public string? Phone { get; set; }
    public bool DefaultAddress { get; set; }
    public bool UseForShipping { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public string? Name { get; set; }
}

public class ProductReviewResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public string? Status { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public class CustomerReviewResult : ProductReviewResult
{
    public ReviewProductResult? Product { get; set; }
    public ReviewCustomerResult? Customer { get; set; }
}

public class ReviewProductResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Sku { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? BaseImageUrl { get; set; }
}

public class ReviewCustomerResult { public int Id { get; set; } [GraphQLName("_id")] public int _Id { get; set; } }

public class WishlistResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public WishlistProductResult? Product { get; set; }
    public ReviewCustomerResult? Customer { get; set; }
    public WishlistChannelResult? Channel { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public class WishlistProductResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? Name { get; set; }
    public decimal Price { get; set; }
    public string Sku { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Description { get; set; }
    public string? BaseImageUrl { get; set; }
    public string? UrlKey { get; set; }
}

public class WishlistChannelResult
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public TranslationResult? Translation { get; set; }
}

public class OrderResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? IncrementId { get; set; }
    public string? Status { get; set; }
    public string? ChannelName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerFirstName { get; set; }
    public string? CustomerLastName { get; set; }
    public int? TotalItemCount { get; set; }
    public int? TotalQtyOrdered { get; set; }
    public decimal? GrandTotal { get; set; }
    public decimal? BaseGrandTotal { get; set; }
    public decimal? SubTotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? ShippingAmount { get; set; }
    public string? ShippingTitle { get; set; }
    public string? CouponCode { get; set; }
    public string? OrderCurrencyCode { get; set; }
    public string? BaseCurrencyCode { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public class OrderDetailResult : OrderResult
{
    public string? ShippingMethod { get; set; }
    public decimal? GrandTotalInvoiced { get; set; }
    public decimal? GrandTotalRefunded { get; set; }
    public decimal? BaseSubTotal { get; set; }
    public decimal? BaseTaxAmount { get; set; }
    public decimal? BaseDiscountAmount { get; set; }
    public decimal? BaseShippingAmount { get; set; }
    public string? ChannelCurrencyCode { get; set; }
    public OrderPaymentResult? Payment { get; set; }
    public Connection<OrderItemResult>? Items { get; set; }
    public Connection<AddressResult>? Addresses { get; set; }
}

public class OrderItemResult
{
    public int Id { get; set; }
    public string? Sku { get; set; }
    public string? Name { get; set; }
    public decimal? QtyOrdered { get; set; }
    public decimal? QtyShipped { get; set; }
    public decimal? QtyInvoiced { get; set; }
    public decimal? QtyCanceled { get; set; }
    public decimal? QtyRefunded { get; set; }
}

public class OrderPaymentResult { public int Id { get; set; } public string? MethodTitle { get; set; } }

public class InvoiceResult
{
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? IncrementId { get; set; }
    public string? State { get; set; }
    public int? TotalQty { get; set; }
    public string? OrderCurrencyCode { get; set; }
    public decimal? GrandTotal { get; set; }
    public decimal? BaseGrandTotal { get; set; }
    public decimal? SubTotal { get; set; }
    public decimal? BaseSubTotal { get; set; }
    public decimal? ShippingAmount { get; set; }
    public decimal? BaseShippingAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? BaseTaxAmount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? BaseDiscountAmount { get; set; }
    public string? BaseCurrencyCode { get; set; }
    public string? TransactionId { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public Connection<InvoiceItemResult>? Items { get; set; }
}

public class InvoiceItemResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? Sku { get; set; }
    public string? Name { get; set; }
    public int? Qty { get; set; }
    public decimal? Price { get; set; }
    public decimal? BasePrice { get; set; }
    public decimal? Total { get; set; }
    public decimal? BaseTotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? BaseTaxAmount { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? DiscountAmount { get; set; }
    public int? ProductId { get; set; }
    public string? ProductType { get; set; }
    public int? OrderItemId { get; set; }
    public int? InvoiceId { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public class ShipmentResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? Status { get; set; }
    public string? TrackNumber { get; set; }
    public string? CarrierTitle { get; set; }
    public int? TotalQty { get; set; }
    public string? ShippingNumber { get; set; }
    public string? CreatedAt { get; set; }
    public Connection<ShipmentItemResult>? Items { get; set; }
}

public class ShipmentItemResult { public int Id { get; set; } public string? Name { get; set; } public string? Sku { get; set; } public int? Qty { get; set; } }

public class LocaleResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "ltr";
}

public class DownloadableResult
{
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? ProductName { get; set; }
    public string? Name { get; set; }
    public string? FileName { get; set; }
    public string Type { get; set; } = "";
    public int DownloadBought { get; set; }
    public int DownloadUsed { get; set; }
    public int DownloadCanceled { get; set; }
    public string? Status { get; set; }
    public int RemainingDownloads { get; set; }
    public DownloadableOrderResult? Order { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public class DownloadableOrderResult { [GraphQLName("_id")] public int _Id { get; set; } public string? IncrementId { get; set; } public string? Status { get; set; } }

public class CmsPageResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? Layout { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public CmsTranslationResult? Translation { get; set; }
}

public class CmsTranslationResult
{
    public int Id { get; set; }
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? PageTitle { get; set; }
    public string? UrlKey { get; set; }
    public string? HtmlContent { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public string? Locale { get; set; }
}

public class CompareItemResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public CompareProductResult? Product { get; set; }
    public CompareCustomerResult? Customer { get; set; }
}

public class CompareProductResult
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public string? BaseImageUrl { get; set; }
    public string? UrlKey { get; set; }
}

public class CompareCustomerResult
{
    public int Id { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}
