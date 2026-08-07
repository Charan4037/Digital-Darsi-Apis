using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Vendor;
using DOSApi.Models.Catalog;
using DOSApi.Models.Sales;
using DOSApi.Models.Customer;
using DOSApi.Services;

namespace DOSApi.Controllers.Vendor;

/// <summary>
/// Vendor APIs - accessed through vendor authentication.
/// All endpoints require vendor authentication token.
/// </summary>
[ApiController]
[Route("api/v1/vendor")]
[Route("api/vendor")] // some app clients call without the /v1/ segment — accept both
[Tags("Vendor")]
[Authorize]
public class VendorController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly OrderInvoiceService _invoiceService;

    public VendorController(DOSDbContext db, OrderInvoiceService invoiceService)
    {
        _db = db;
        _invoiceService = invoiceService;
    }

    private static Address? PickDeliveryAddress(List<Address> addresses) =>
        addresses.FirstOrDefault(a => a.AddressType == "order_shipping")
        ?? addresses.FirstOrDefault(a => a.UseForShipping)
        ?? addresses.FirstOrDefault();

    private static string? FormatAddress(Address? a) => a == null
        ? null
        : string.Join(", ", new[] { a.AddressLine, a.City, a.State, a.Postcode }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    // Order.CustomerFirstName/LastName are sometimes blank (a checkout-flow gap that
    // predates the fix in CheckoutService) — fall back to the address name, which is
    // always populated, for those orders.
    private static string ResolveCustomerName(Order o, Address? addr)
    {
        var name = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim();
        if (string.IsNullOrWhiteSpace(name) && addr != null)
            name = $"{addr.FirstName} {addr.LastName}".Trim();
        return name;
    }

    private int GetVendorId()
    {
        var vendorIdClaim = User.FindFirst("vendor_id")?.Value ?? "0";
        return int.TryParse(vendorIdClaim, out var vendorId) ? vendorId : 0;
    }

    // ?????????????????????????????????????????????????????????????????????????
    // 1. DASHBOARD
    // ?????????????????????????????????????????????????????????????????????????

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        var vendor = await _db.Customers.FirstOrDefaultAsync(v => v.Id == vendorId);
        if (vendor == null) return NotFound(new { message = "Vendor not found" });

        var totalProducts = await _db.Products.CountAsync(p => p.Inventories.Any(inv => inv.VendorId == vendorId));
        var activeProducts = await _db.Products.CountAsync(p => p.Inventories.Any(inv => inv.VendorId == vendorId) && p.Flats.Any(f => f.Status == true));
        var totalOrders = await _db.Orders.CountAsync(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)));
        var pendingOrders = await _db.Orders.CountAsync(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)) && o.Status == "pending");

        var totalRevenue = await _db.Orders
            .Where(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)))
            .SumAsync(o => (decimal?)(o.GrandTotal ?? 0)) ?? 0;

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var monthRevenue = await _db.Orders
            .Where(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)) && o.CreatedAt >= monthStart)
            .SumAsync(o => (decimal?)(o.GrandTotal ?? 0)) ?? 0;

        var recentOrders = await _db.Orders
            .Where(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)))
            .OrderByDescending(o => o.CreatedAt)
            .Take(4)
            .Select(o => new RecentOrderDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                ItemsCount = o.TotalItemCount ?? 0,
                CustomerName = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim(),
                CustomerPhone = o.CustomerEmail,
                DeliveryAddress = o.Addresses.FirstOrDefault() != null ? o.Addresses.FirstOrDefault().AddressLine : null,
                PaymentMethod = o.Payment != null ? o.Payment.Method : null,
                ShippingMethod = o.ShippingTitle
            })
            .ToListAsync();

        return Ok(new DashboardResponse
        {
            Data = new DashboardData
            {
                Store = new StoreCardDto
                {
                    Id = vendor.Id,
                    StoreName = vendor.FirstName,
                    IsActive = vendor.Status == 1,
                    AvgRating = 4.3m,
                    TotalReviews = 0
                },
                Stats = new StatsGridDto
                {
                    TotalProducts = totalProducts,
                    ActiveProducts = activeProducts,
                    TotalOrders = totalOrders,
                    PendingOrders = pendingOrders,
                    TotalRevenue = totalRevenue,
                    MonthRevenue = monthRevenue
                },
                RecentOrders = recentOrders
            }
        });
    }

    // ?????????????????????????????????????????????????????????????????????????
    // 2. STORE PROFILE
    // ?????????????????????????????????????????????????????????????????????????

    [HttpGet("store")]
    public async Task<IActionResult> GetStoreProfile()
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        var vendor = await _db.Customers.FirstOrDefaultAsync(v => v.Id == vendorId);
        if (vendor == null) return NotFound(new { message = "Vendor not found" });

        var totalProducts = await _db.Products.CountAsync(p => p.Inventories.Any(inv => inv.VendorId == vendorId));
        var totalOrders = await _db.Orders.CountAsync(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)));

        return Ok(new StoreProfileResponse
        {
            Data = new StoreProfileDto
            {
                Id = vendor.Id,
                StoreName = vendor.FirstName,
                OwnerName = vendor.LastName,
                // customers has no description/address column in the real schema —
                // these fields have no backing store until a dedicated vendor
                // profile table exists.
                Description = null,
                Phone = vendor.Phone,
                Email = vendor.Email,
                Address = null,
                LogoUrl = vendor.Image,
                IsActive = vendor.Status == 1,
                Stats = new StoreStatsDto
                {
                    AvgRating = 4.3m,
                    TotalReviews = 0,
                    TotalProducts = totalProducts,
                    TotalOrders = totalOrders
                }
            }
        });
    }

    [HttpPut("store")]
    public async Task<IActionResult> UpdateStoreProfile([FromBody] UpdateStoreProfileRequest request)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.StoreName))
            return BadRequest(new { message = "storeName is required" });

        var vendor = await _db.Customers.FirstOrDefaultAsync(v => v.Id == vendorId);
        if (vendor == null) return NotFound(new { message = "Vendor not found" });

        vendor.FirstName = request.StoreName;
        vendor.LastName = request.OwnerName ?? "";
        vendor.Phone = request.Phone;
        vendor.Email = request.Email;
        vendor.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // customers has no description/address column in the real schema — echo
        // back what was submitted rather than a persisted value.
        return Ok(new { data = new { id = vendor.Id, storeName = vendor.FirstName, ownerName = vendor.LastName, description = request.Description, phone = vendor.Phone, email = vendor.Email, address = request.Address, logoUrl = vendor.Image, isActive = vendor.Status == 1 }, message = "Store profile updated" });
    }

    [HttpPatch("store/status")]
    public async Task<IActionResult> UpdateStoreStatus([FromBody] UpdateStoreStatusRequest request)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        var vendor = await _db.Customers.FirstOrDefaultAsync(v => v.Id == vendorId);
        if (vendor == null) return NotFound(new { message = "Vendor not found" });

        vendor.Status = request.IsActive ? 1 : 0;
        vendor.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { data = new { isActive = request.IsActive }, message = $"Store is now {(request.IsActive ? "Active" : "Inactive")}" });
    }

    // ?????????????????????????????????????????????????????????????????????????
    // 3. PRODUCTS
    // ?????????????????????????????????????????????????????????????????????????

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? stock = null)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Products
            .Where(p => p.Inventories.Any(inv => inv.VendorId == vendorId))
            .Include(p => p.Flats)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(p =>
                p.Flats.Any(f => f.Name != null && f.Name.ToLower().Contains(searchLower)));
        }

        if (!string.IsNullOrWhiteSpace(stock) && stock != "all")
        {
            if (stock == "inStock")
                query = query.Where(p => p.Flats.Any(f => f.Status == true));
            else if (stock == "outOfStock")
                query = query.Where(p => !p.Flats.Any(f => f.Status == true));
        }

        var total = await query.CountAsync();
        var products = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = products.Select(p => new VendorProductDto
        {
            Id = p.Id,
            Sku = p.Sku,
            Name = p.Flats.FirstOrDefault()?.Name ?? "",
            Description = p.Flats.FirstOrDefault()?.Description,
            Price = p.Flats.FirstOrDefault()?.Price ?? 0,
            SpecialPrice = null,
            Image = p.Images.FirstOrDefault()?.Path,
            InStock = p.Flats.FirstOrDefault()?.Status == true,
            StockQty = 0,
            CategoryName = p.Categories.FirstOrDefault()?.Translations.FirstOrDefault()?.Name,
            AvgRating = 0,
            ReviewsCount = 0
        }).ToList();

        return Ok(new ProductListResponse
        {
            Data = data,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (int)Math.Ceiling(total / (double)limit),
                PerPage = limit
            }
        });
    }

    // ?????????????????????????????????????????????????????????????????????????
    // 4. CATEGORIES
    // ?????????????????????????????????????????????????????????????????????????

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories([FromQuery] int page = 1, [FromQuery] int limit = 50)
    {
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 50;

        var query = _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Products)
            .AsNoTracking();

        var total = await query.CountAsync();
        var categories = await query
            .OrderBy(c => c.Position)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = categories.Select(c => new VendorCategoryDto
        {
            Id = c.Id,
            Name = c.Translations.FirstOrDefault()?.Name ?? "",
            Slug = c.Translations.FirstOrDefault()?.Slug ?? "",
            Description = c.Translations.FirstOrDefault()?.Description,
            Active = c.Status,
            ProductCount = c.Products.Count
        }).ToList();

        return Ok(new CategoryListResponse
        {
            Data = data,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (int)Math.Ceiling(total / (double)limit),
                PerPage = limit
            }
        });
    }

    // ?????????????????????????????????????????????????????????????????????????
    // 5. ORDERS
    // ?????????????????????????????????????????????????????????????????????????

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Orders
            .Where(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)))
            .Include(o => o.Payment)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(o => o.Status == status.ToLower());
        }

        var total = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        // Addresses aren't a real EF navigation on Order (see BagistoDbContext), so load
        // them separately and group by order id, same pattern as AdminOrderController.
        var orderIds = orders.Select(o => o.Id).ToList();
        var addressesByOrder = await _db.Addresses
            .Where(a => a.OrderId != null && orderIds.Contains(a.OrderId.Value))
            .AsNoTracking()
            .ToListAsync();

        var data = orders.Select(o =>
        {
            var deliveryAddr = PickDeliveryAddress(addressesByOrder.Where(a => a.OrderId == o.Id).ToList());
            return new VendorOrderListDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                ItemsCount = o.TotalItemCount ?? 0,
                CustomerName = ResolveCustomerName(o, deliveryAddr),
                CustomerPhone = deliveryAddr?.Phone,
                DeliveryAddress = FormatAddress(deliveryAddr),
                PaymentMethod = o.Payment?.Method,
                ShippingMethod = o.ShippingTitle
            };
        }).ToList();

        return Ok(new OrderListResponse
        {
            Data = data,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (int)Math.Ceiling(total / (double)limit),
                PerPage = limit
            }
        });
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrderDetail(int id)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        var order = await _db.Orders
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .ThenInclude(p => p!.Inventories)
            .Include(o => o.Payment)
            .Include(o => o.ExtraCharges)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound(new { message = "Order not found" });

        var vendorItems = order.Items.Where(i => i.Product?.Inventories.Any(inv => inv.VendorId == vendorId) ?? false).ToList();
        if (vendorItems.Count == 0) return Unauthorized(new { message = "You do not have access to this order" });

        var items = vendorItems
            .Where(i => i.ParentId == null)
            .Select(i => new OrderItemDto
            {
                Name = i.Name ?? "",
                Qty = (int)(i.QtyOrdered ?? 0),
                Price = i.Price ?? 0,
                Image = null
            }).ToList();

        // Addresses aren't a real EF navigation on Order (see BagistoDbContext), so load
        // them separately, same pattern as AdminOrderController / OrderInvoiceService.
        var addresses = await _db.Addresses
            .Where(a => a.OrderId == id)
            .AsNoTracking()
            .ToListAsync();
        var deliveryAddr = PickDeliveryAddress(addresses);

        return Ok(new OrderDetailResponse
        {
            Data = new VendorOrderDetailDto
            {
                Id = order.Id,
                IncrementId = order.IncrementId ?? "",
                PlacedAt = order.CreatedAt ?? DateTime.UtcNow,
                Status = order.Status ?? "pending",
                ItemsCount = vendorItems.Count,
                CustomerName = ResolveCustomerName(order, deliveryAddr),
                CustomerPhone = deliveryAddr?.Phone,
                CustomerEmail = order.CustomerEmail,
                DeliveryAddress = FormatAddress(deliveryAddr),
                Addresses = addresses.Select(a => new VendorAddressDto
                {
                    AddressType = a.AddressType,
                    FirstName = a.FirstName,
                    LastName = a.LastName,
                    Phone = a.Phone,
                    Email = a.Email,
                    AddressLine = a.AddressLine,
                    City = a.City,
                    State = a.State,
                    Postcode = a.Postcode,
                    Country = a.Country
                }).ToList(),
                PaymentMethod = order.Payment?.Method,
                ShippingMethod = order.ShippingTitle,
                Items = items,
                ItemsTotal = vendorItems.Sum(i => i.Total ?? 0),
                SubTotal = order.SubTotal ?? 0,
                TaxAmount = order.TaxAmount ?? 0,
                ShippingAmount = order.ShippingAmount ?? 0,
                DiscountAmount = order.DiscountAmount ?? 0,
                ExtraChargesTotal = order.ExtraChargesTotal ?? 0,
                GrandTotal = order.GrandTotal ?? 0
            }
        });
    }

    /// <summary>Download the PDF invoice for an order containing this vendor's items.</summary>
    [HttpGet("orders/{id:int}/invoice")]
    public async Task<IActionResult> DownloadInvoice(int id)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        var order = await _db.Orders
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .ThenInclude(p => p!.Inventories)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound(new { message = "Order not found" });

        if (!order.Items.Any(i => i.Product?.Inventories.Any(inv => inv.VendorId == vendorId) ?? false))
            return Unauthorized(new { message = "You do not have access to this order" });

        try
        {
            var pdfBytes = await _invoiceService.GenerateInvoicePdfAsync(id);
            if (pdfBytes == null || pdfBytes.Length == 0)
                return BadRequest(new { message = "Failed to generate invoice PDF - empty result." });

            return File(pdfBytes, "application/pdf", $"invoice_order_{id}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = $"Invoice generation failed: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error generating invoice", error = ex.Message, type = ex.GetType().Name });
        }
    }

    [HttpPatch("orders/{id:int}/status")]
    public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] VendorUpdateOrderStatusRequest request)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Status))
            return BadRequest(new { message = "status is required" });

        var order = await _db.Orders
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .ThenInclude(p => p!.Inventories)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound(new { message = "Order not found" });

        if (!order.Items.Any(i => i.Product?.Inventories.Any(inv => inv.VendorId == vendorId) ?? false))
            return Unauthorized(new { message = "You do not have access to this order" });

        var newStatus = request.Status.ToLower();
        var validStatuses = new[] { "processing", "shipped", "completed" };

        if (!validStatuses.Contains(newStatus))
            return BadRequest(new { message = $"Invalid status: {newStatus}" });

        var statusTransitions = new Dictionary<string, string[]>
        {
            { "pending", new[] { "processing", "cancelled" } },
            { "processing", new[] { "shipped", "cancelled" } },
            { "shipped", new[] { "completed" } },
            { "completed", System.Array.Empty<string>() },
            { "cancelled", System.Array.Empty<string>() }
        };

        var currentStatus = order.Status?.ToLower() ?? "pending";

        if (!statusTransitions.ContainsKey(currentStatus))
            return BadRequest(new { message = $"Cannot update status from '{currentStatus}'" });

        if (!statusTransitions[currentStatus].Contains(newStatus))
            return BadRequest(new { message = $"Cannot update status from '{currentStatus}'" });

        order.Status = newStatus;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new OrderStatusResponse
        {
            Data = new OrderStatusData { Id = order.Id, Status = newStatus },
            Message = $"Order marked as {newStatus}"
        });
    }

    [HttpPost("orders/{id:int}/cancel")]
    public async Task<IActionResult> CancelOrder(int id, [FromBody] CancelOrderRequest request)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        var order = await _db.Orders
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .ThenInclude(p => p!.Inventories)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null) return NotFound(new { message = "Order not found" });

        if (!order.Items.Any(i => i.Product?.Inventories.Any(inv => inv.VendorId == vendorId) ?? false))
            return Unauthorized(new { message = "You do not have access to this order" });

        var currentStatus = order.Status?.ToLower() ?? "pending";
        if (currentStatus == "completed" || currentStatus == "cancelled")
            return BadRequest(new { message = "Cannot cancel a completed or already cancelled order" });

        order.Status = "cancelled";
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new OrderStatusResponse
        {
            Data = new OrderStatusData { Id = order.Id, Status = "cancelled" },
            Message = "Order cancelled"
        });
    }

    // ?????????????????????????????????????????????????????????????????????????
    // 6. TRANSACTIONS
    // ?????????????????????????????????????????????????????????????????????????

    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        var vendorId = GetVendorId();
        if (vendorId == 0) return Unauthorized();

        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var allTransactions = await _db.Orders
            .Where(o => o.Items.Any(i => i.Product.Inventories.Any(inv => inv.VendorId == vendorId)))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new TransactionDto
            {
                Id = o.Id,
                OrderId = o.IncrementId ?? "",
                Date = o.CreatedAt ?? DateTime.UtcNow,
                Amount = o.GrandTotal ?? 0,
                Type = "credit",
                Status = o.Status == "completed" ? "settled" : "pending",
                PaymentMethod = o.Payment != null ? o.Payment.Method ?? "Unknown" : "Unknown",
                Note = null
            })
            .ToListAsync();

        var total = allTransactions.Count;
        var transactions = allTransactions
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToList();

        var summary = new TransactionSummaryDto
        {
            TotalEarnings = allTransactions.Where(t => t.Type == "credit" && t.Status == "settled").Sum(t => t.Amount),
            PendingSettlement = allTransactions.Where(t => t.Type == "credit" && t.Status == "pending").Sum(t => t.Amount),
            TotalRefunds = allTransactions.Where(t => t.Type == "debit").Sum(t => t.Amount)
        };

        return Ok(new TransactionListResponse
        {
            Data = transactions,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (int)Math.Ceiling(total / (double)limit),
                PerPage = limit
            },
            Summary = summary
        });
    }
}
