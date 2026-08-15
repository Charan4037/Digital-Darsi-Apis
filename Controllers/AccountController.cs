using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers;

[ApiController]
[Route("api/v1/customer")]
[Authorize]
[Tags("Customer Account")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AccountController : ControllerBase
{
    private readonly AccountService _accountService;
    private readonly AuthService _authService;
    private readonly CartService _cartService;
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    public AccountController(AccountService accountService, AuthService authService,
        CartService cartService, DOSDbContext db, ProductService productService)
    {
        _accountService = accountService;
        _authService = authService;
        _cartService = cartService;
        _db = db;
        _productService = productService;
    }

    // ─── Profile ─────────────────────────────────────────────────────────

    public record ProfileUpdateRequest(string? FirstName, string? LastName, string? Phone,
        string? Gender, string? DateOfBirth, string? Email, string? CurrentPassword, string? NewPassword, bool? Newsletter);
    public record DeleteAccountRequest(string? Password = null);

    /// <summary>Get customer profile</summary>
    [HttpGet("get")]
    public async Task<IActionResult> GetProfile()
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var customer = await _accountService.GetProfileAsync(customerId.Value);
        if (customer == null) return NotFound();

        return Ok(new
        {
            data = new
            {
                customer.Id, customer.FirstName, customer.LastName, customer.Email,
                customer.Phone, customer.Gender, customer.DateOfBirth,
                customer.SubscribedToNewsLetter, customer.Image, customer.CreatedAt
            }
        });
    }

    /// <summary>Update customer profile</summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] ProfileUpdateRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        await _accountService.UpdateProfileAsync(customerId.Value, req.FirstName, req.LastName,
            req.Phone, req.Gender, req.DateOfBirth, req.Newsletter);

        if (!string.IsNullOrEmpty(req.Email) && !string.IsNullOrEmpty(req.CurrentPassword))
        {
            var (success, message) = await _accountService.ChangeEmailAsync(customerId.Value, req.Email, req.CurrentPassword);
            if (!success) return BadRequest(new { success, message });
        }

        if (!string.IsNullOrEmpty(req.NewPassword) && !string.IsNullOrEmpty(req.CurrentPassword))
        {
            var (success, message) = await _accountService.ChangePasswordAsync(customerId.Value, req.CurrentPassword, req.NewPassword);
            if (!success) return BadRequest(new { success, message });
        }

        return Ok(new { success = true, message = "Profile updated successfully." });
    }

    /// <summary>Delete customer account</summary>
    [HttpDelete("profile")]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var (success, message) = await _accountService.DeleteAccountAsync(customerId.Value, req.Password);
        if (!success) return BadRequest(new { success, message });
        return Ok(new { success, message });
    }

    // ─── Addresses ──────────────────────────────────────────────────────

    public record AddressRequest(string FirstName, string LastName, string Address, string City,
        string State, string Country, string Postcode, string Phone, string? Email,
        bool UseForShipping = false, bool DefaultAddress = false);

    /// <summary>List customer addresses</summary>
    [HttpGet("addresses")]
    public async Task<IActionResult> GetAddresses([FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var q = _accountService.GetAddresses(customerId.Value);
        var total = await q.CountAsync();
        var data = await q.Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = data.Select(a => new
            {
                a.Id, a.FirstName, a.LastName, a.AddressLine, a.City, a.State,
                a.Country, a.Postcode, a.Phone, a.Email, a.DefaultAddress
            }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    /// <summary>Create a new customer address</summary>
    [HttpPost("addresses")]
    public async Task<IActionResult> CreateAddress([FromBody] AddressRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var (success, message, addr) = await _accountService.AddOrUpdateAddressAsync(customerId.Value, null,
            req.FirstName, req.LastName, req.Address, req.City, req.State,
            req.Country, req.Postcode, req.Phone, req.Email, req.UseForShipping, req.DefaultAddress);
        if (!success) return BadRequest(new { success = false, message });

        return Ok(new { success = true, message, data = new { addr!.Id } });
    }

    /// <summary>Update a customer address</summary>
    [HttpPut("addresses/edit/{id:int}")]
    public async Task<IActionResult> UpdateAddress(int id, [FromBody] AddressRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var (success, message, addr) = await _accountService.AddOrUpdateAddressAsync(customerId.Value, id,
            req.FirstName, req.LastName, req.Address, req.City, req.State,
            req.Country, req.Postcode, req.Phone, req.Email, req.UseForShipping, req.DefaultAddress);
        if (!success) return BadRequest(new { success = false, message });

        return Ok(new { success = true, message, data = new { addr!.Id } });
    }

    /// <summary>Delete a customer address</summary>
    [HttpDelete("addresses/{id:int}")]
    public async Task<IActionResult> DeleteAddress(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var (success, message) = await _accountService.DeleteAddressAsync(customerId.Value, id);
        if (!success) return NotFound(new { success, message });
        return Ok(new { success, message });
    }

    // ─── Orders ─────────────────────────────────────────────────────────

    /// <summary>List customer orders</summary>
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var q = _accountService.GetOrders(customerId.Value, status);
        var total = await q.CountAsync();
        var orders = await q.Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = orders.Select(o => new
            {
                o.Id, o.IncrementId, o.Status, o.GrandTotal, o.SubTotal,
                o.TaxAmount, o.DiscountAmount, o.ShippingAmount,
                o.TotalItemCount, o.TotalQtyOrdered, o.CustomerEmail,
                o.CustomerFirstName, o.CustomerLastName, o.CreatedAt
            }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    /// <summary>Get order details</summary>
    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrderDetail(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var order = await _accountService.GetOrderDetailAsync(customerId.Value, id);
        if (order == null) return NotFound(new { message = "Order not found." });

        var addresses = await _accountService.GetOrderAddressesAsync(id);

        return Ok(new
        {
            data = new
            {
                order.Id, order.IncrementId, order.Status, order.GrandTotal, order.SubTotal,
                order.TaxAmount, order.DiscountAmount, order.ShippingAmount, order.ShippingTitle,
                order.CouponCode, order.TotalItemCount, order.TotalQtyOrdered,
                order.CustomerEmail, order.CustomerFirstName, order.CustomerLastName,
                Items = order.Items.Select(i => new
                {
                    i.Id, i.Sku, i.Name, i.QtyOrdered, i.Price, i.Total,
                    i.TaxAmount, i.DiscountAmount, i.ProductId, i.Type
                }),
                Payment = order.Payment != null ? new { order.Payment.Method, order.Payment.MethodTitle } : null,
                Addresses = addresses.Select(a => new
                {
                    a.AddressType, a.FirstName, a.LastName, a.AddressLine,
                    a.City, a.State, a.Country, a.Postcode, a.Phone, a.Email
                }),
                order.CreatedAt
            }
        });
    }

    /// <summary>Reorder — add items from a previous order to cart</summary>
    [HttpPost("orders/{id:int}/reorder")]
    public async Task<IActionResult> Reorder(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var (success, message, orderId, count) = await _accountService.ReorderAsync(customerId.Value, id);
        if (!success) return BadRequest(new { success, message });
        return Ok(new { success, message, itemsAdded = count });
    }

    // ─── Invoices ───────────────────────────────────────────────────────

    /// <summary>List customer invoices</summary>
    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] int? orderId, [FromQuery] string? state,
        [FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var q = _accountService.GetInvoices(customerId.Value, orderId, state);
        var total = await q.CountAsync();
        var invoices = await q.Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = invoices.Select(i => new
            {
                i.Id, i.OrderId, i.State, i.GrandTotal, i.SubTotal,
                i.TaxAmount, i.DiscountAmount, i.ShippingAmount, i.CreatedAt
            }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    /// <summary>Get invoice details</summary>
    [HttpGet("invoices/{id:int}")]
    public async Task<IActionResult> GetInvoiceDetail(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var invoice = await _accountService.GetInvoiceDetailAsync(customerId.Value, id);
        if (invoice == null) return NotFound(new { message = "Invoice not found." });

        return Ok(new
        {
            data = new
            {
                invoice.Id, invoice.OrderId, invoice.State, invoice.GrandTotal,
                invoice.SubTotal, invoice.TaxAmount, invoice.DiscountAmount,
                Items = invoice.Items.Select(i => new
                {
                    i.Id, i.Name, i.Sku, i.Qty, i.Price, i.Total, i.TaxAmount
                }),
                invoice.CreatedAt
            }
        });
    }

    // ─── Shipments ──────────────────────────────────────────────────────

    /// <summary>List shipments for an order</summary>
    [HttpGet("orders/{orderId:int}/shipments")]
    public async Task<IActionResult> GetShipments(int orderId)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var shipments = await _accountService.GetShipments(orderId, customerId.Value).ToListAsync();

        return Ok(new
        {
            data = shipments.Select(s => new
            {
                s.Id, s.OrderId, s.TrackNumber, s.TotalQty,
                Items = s.Items.Select(i => new { i.Id, i.Name, i.Sku, i.Qty }),
                s.CreatedAt
            })
        });
    }

    /// <summary>Get shipment details</summary>
    [HttpGet("shipments/{id:int}")]
    public async Task<IActionResult> GetShipmentDetail(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var shipment = await _accountService.GetShipmentDetailAsync(customerId.Value, id);
        if (shipment == null) return NotFound(new { message = "Shipment not found." });

        return Ok(new
        {
            data = new
            {
                shipment.Id, shipment.OrderId, shipment.TrackNumber, shipment.TotalQty,
                Items = shipment.Items.Select(i => new { i.Id, i.Name, i.Sku, i.Qty }),
                shipment.CreatedAt
            }
        });
    }

    // ─── Wishlist ───────────────────────────────────────────────────────

    public record WishlistRequest(int ProductId);

    /// <summary>Get wishlist items</summary>
    [HttpGet("wishlist")]
    public async Task<IActionResult> GetWishlist([FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var q = _db.Wishlists
            .Include(w => w.Product).ThenInclude(p => p!.Images)
            .Include(w => w.Product).ThenInclude(p => p!.AttributeValues)
            .Where(w => w.CustomerId == customerId.Value);

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(w => w.Id).Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = items.Select(w => new
            {
                w.Id,
                w.ProductId,
                Product = w.Product != null ? new
                {
                    w.Product.Id,
                    Name = _productService.GetProductName(w.Product),
                    Price = _productService.GetProductPrice(w.Product),
                    SpecialPrice = _productService.GetProductSpecialPrice(w.Product),
                    BaseImage = _productService.GetBaseImageUrl(w.Product)
                } : null,
                w.CreatedAt
            }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    /// <summary>Add product to wishlist</summary>
    [HttpPost("wishlist")]
    public async Task<IActionResult> AddToWishlist([FromBody] WishlistRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var exists = await _db.Wishlists.AnyAsync(w => w.CustomerId == customerId.Value && w.ProductId == req.ProductId);
        if (exists) return Ok(new { success = true, message = "Product already in wishlist." });

        _db.Wishlists.Add(new Models.Customer.Wishlist
        {
            CustomerId = customerId.Value,
            ProductId = req.ProductId,
            ChannelId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Product added to wishlist." });
    }

    /// <summary>Remove product from wishlist</summary>
    [HttpDelete("wishlist/{id:int}")]
    public async Task<IActionResult> RemoveFromWishlist(int id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var item = await _db.Wishlists.FirstOrDefaultAsync(w => w.Id == id && w.CustomerId == customerId.Value);
        if (item == null) return NotFound(new { success = false, message = "Wishlist item not found." });

        _db.Wishlists.Remove(item);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Item removed from wishlist." });
    }

    /// <summary>Clear all wishlist items</summary>
    [HttpDelete("wishlist/all")]
    public async Task<IActionResult> ClearWishlist()
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var items = await _db.Wishlists.Where(w => w.CustomerId == customerId.Value).ToListAsync();
        _db.Wishlists.RemoveRange(items);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Wishlist cleared." });
    }

    /// <summary>Move wishlist item to cart</summary>
    [HttpPost("wishlist/{id:int}/move-to-cart")]
    public async Task<IActionResult> MoveWishlistToCart(int id, [FromQuery] int quantity = 1)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var item = await _db.Wishlists.FirstOrDefaultAsync(w => w.Id == id && w.CustomerId == customerId.Value);
        if (item == null) return NotFound(new { success = false, message = "Wishlist item not found." });

        var cart = await _cartService.GetCartAsync(customerId.Value, null);
        if (cart == null)
        {
            var (newCart, _, _, _) = await _cartService.CreateCartAsync(customerId.Value);
            cart = newCart;
        }

        var (_, success, message) = await _cartService.AddToCartAsync(cart, item.ProductId, quantity);
        if (success)
        {
            _db.Wishlists.Remove(item);
            await _db.SaveChangesAsync();
        }

        return Ok(new { success, message });
    }

    // ─── Compare ────────────────────────────────────────────────────────

    public record CompareRequest(int ProductId);

    /// <summary>Get compare items</summary>
    [HttpGet("~/api/v1/compare-items")]
    public async Task<IActionResult> GetCompareItems()
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var items = await _db.CompareItems
            .Include(c => c.Product).ThenInclude(p => p!.Images)
            .Include(c => c.Product).ThenInclude(p => p!.AttributeValues)
            .Where(c => c.CustomerId == customerId.Value)
            .ToListAsync();

        return Ok(new
        {
            data = items.Select(c => new
            {
                c.Id,
                c.ProductId,
                Product = c.Product != null ? new
                {
                    c.Product.Id,
                    Name = _productService.GetProductName(c.Product),
                    Price = _productService.GetProductPrice(c.Product),
                    BaseImage = _productService.GetBaseImageUrl(c.Product)
                } : null
            })
        });
    }

    /// <summary>Add product to compare list</summary>
    [HttpPost("~/api/v1/compare-items")]
    public async Task<IActionResult> AddCompareItem([FromBody] CompareRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var exists = await _db.CompareItems.AnyAsync(c => c.CustomerId == customerId.Value && c.ProductId == req.ProductId);
        if (exists) return Ok(new { success = true, message = "Product already in compare list." });

        _db.CompareItems.Add(new Models.Customer.CompareItem
        {
            CustomerId = customerId.Value,
            ProductId = req.ProductId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Product added to compare list." });
    }

    /// <summary>Remove product from compare list</summary>
    [HttpDelete("~/api/v1/compare-items")]
    public async Task<IActionResult> RemoveCompareItem([FromQuery] int? productId, [FromQuery] int? id)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var itemId = productId ?? id;
        if (!itemId.HasValue) return BadRequest(new { success = false, message = "productId or id is required." });

        var item = await _db.CompareItems.FirstOrDefaultAsync(c =>
            (c.ProductId == itemId.Value || c.Id == itemId.Value) && c.CustomerId == customerId.Value);
        if (item == null) return NotFound(new { success = false, message = "Compare item not found." });

        _db.CompareItems.Remove(item);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Item removed from compare list." });
    }

    /// <summary>Clear all compare items</summary>
    [HttpDelete("~/api/v1/compare-items/all")]
    public async Task<IActionResult> ClearCompareItems()
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var items = await _db.CompareItems.Where(c => c.CustomerId == customerId.Value).ToListAsync();
        _db.CompareItems.RemoveRange(items);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Compare list cleared." });
    }

    // ─── Reviews ────────────────────────────────────────────────────────

    public record ReviewRequest(string Title, string Comment, int Rating, string? Name);

    /// <summary>Get reviews for a product</summary>
    [HttpGet("~/api/v1/product/{id:int}/reviews")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProductReviews(int id, [FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var q = _db.ProductReviews.Where(r => r.ProductId == id && r.Status == "approved");
        var total = await q.CountAsync();
        var reviews = await q.OrderByDescending(r => r.Id).Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = reviews.Select(r => new { r.Id, r.Title, r.Comment, r.Rating, r.Name, r.Status, r.CreatedAt }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    /// <summary>Submit a product review</summary>
    [HttpPost("~/api/v1/product/{id:int}/review")]
    public async Task<IActionResult> CreateReview(int id, [FromBody] ReviewRequest req)
    {
        var customerId = _authService.GetCurrentCustomerId();

        _db.ProductReviews.Add(new Models.Catalog.ProductReview
        {
            ProductId = id,
            CustomerId = customerId,
            Title = req.Title,
            Comment = req.Comment,
            Rating = req.Rating,
            Name = req.Name,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Review submitted successfully." });
    }

    /// <summary>Get authenticated customer's reviews</summary>
    [HttpGet("reviews")]
    public async Task<IActionResult> GetCustomerReviews([FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var q = _db.ProductReviews.Include(r => r.Product).Where(r => r.CustomerId == customerId.Value);
        var total = await q.CountAsync();
        var reviews = await q.OrderByDescending(r => r.Id).Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = reviews.Select(r => new
            {
                r.Id, r.ProductId, r.Title, r.Comment, r.Rating, r.Status, r.CreatedAt,
                ProductName = r.Product != null ? _productService.GetProductName(r.Product) : null
            }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    // ─── Downloadable Products ──────────────────────────────────────────

    /// <summary>Get customer's downloadable product purchases</summary>
    [HttpGet("downloadable-products")]
    public async Task<IActionResult> GetDownloadableProducts([FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var customerId = _authService.GetCurrentCustomerId();
        if (!customerId.HasValue) return Unauthorized();

        var q = _db.DownloadableLinkPurchased.Where(d => d.CustomerId == customerId.Value);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(d => d.Id).Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new
        {
            data = items.Select(d => new
            {
                d.Id, d.ProductName, d.Name, d.Status, d.DownloadBought, d.DownloadUsed, d.CreatedAt
            }),
            meta = new { total, currentPage = page, perPage = limit }
        });
    }

    // ─── Contact Us ─────────────────────────────────────────────────────

    public record ContactUsRequest(string Name, string Email, string Subject, string Message);

    /// <summary>Submit contact form</summary>
    [HttpPost("~/api/v1/contact-us")]
    [AllowAnonymous]
    public IActionResult ContactUs([FromBody] ContactUsRequest req)
    {
        // In production, this would send an email
        return Ok(new { success = true, message = "Your inquiry has been submitted successfully." });
    }
}
