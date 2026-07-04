using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Customer;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin vendor management controller.
/// Routes: /api/v1/admin/vendors
/// </summary>
[Route("api/v1/admin/vendors")]
[Tags("Admin – Vendors")]
public class AdminVendorsController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminVendorsController(DOSDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    /// <summary>List all vendors with search and filter</summary>
    /// <remarks>
    /// Returns paginated list of vendors with their stats.
    /// Supports search by name or city, and filtering by active status.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Customers
            .Include(c => c.Orders)
            .AsNoTracking()
            .Where(c => c.Status > 0); // Active customers

        // Search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(c =>
                (c.FirstName + " " + c.LastName).ToLower().Contains(searchLower) ||
                c.Email!.ToLower().Contains(searchLower));
        }

        // Status filter
        if (!string.IsNullOrWhiteSpace(status))
        {
            bool isActive = status.Equals("active", StringComparison.OrdinalIgnoreCase);
            query = query.Where(c => (c.Status == 1) == isActive);
        }

        var total = await query.CountAsync();
        var vendors = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(c => new VendorDto
            {
                Id = c.Id,
                Name = c.FirstName + " " + c.LastName,
                Email = c.Email ?? "",
                Phone = c.Phone ?? "",
                City = "", // TODO: Add city field to Customer model if needed
                Products = 0, // TODO: Calculate from products
                Orders = c.Orders.Count,
                Revenue = c.Orders.Where(o => o.Status != "canceled").Sum(o => o.GrandTotal ?? 0),
                Rating = 4.3, // TODO: Calculate from reviews
                Active = c.Status == 1,
                JoinedAt = c.CreatedAt ?? DateTime.UtcNow
            })
            .ToListAsync();

        return Ok(new VendorListResponse
        {
            Data = vendors,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            }
        });
    }

    /// <summary>Get vendor detail overview</summary>
    /// <remarks>
    /// Returns complete vendor profile with stats and recent products/orders.
    /// </remarks>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var vendor = await _db.Customers
            .Include(c => c.Orders)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var products = await _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Where(p => p.ParentId == null)
            .AsNoTracking()
            .ToListAsync();

        var recentProducts = products
            .OrderByDescending(p => p.CreatedAt)
            .Take(3)
            .Select(p => new VendorProductDto
            {
                Id = p.Id,
                Name = p.Flats.FirstOrDefault()?.Name ?? "",
                Price = decimal.Parse(p.Flats.FirstOrDefault()?.Price?.ToString() ?? "0"),
                SpecialPrice = p.Flats.FirstOrDefault()?.SpecialPrice,
                CategoryName = "", // TODO: Get from category
                InStock = p.Inventories.Any(i => i.Qty > 0)
            })
            .ToList();

        var recentOrders = vendor.Orders
            .OrderByDescending(o => o.CreatedAt)
            .Take(4)
            .Select(o => new RecentOrderDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
                VendorName = vendor.FirstName + " " + vendor.LastName
            })
            .ToList();

        var completedOrders = vendor.Orders.Count(o => o.Status == "completed");
        var inStockProducts = products.Count(p => p.Inventories.Any(i => i.Qty > 0));

        var response = new VendorDetailResponse
        {
            Data = new VendorDetailDto
            {
                Id = vendor.Id,
                Name = vendor.FirstName + " " + vendor.LastName,
                Email = vendor.Email ?? "",
                Phone = vendor.Phone ?? "",
                City = "", // TODO: Add city field
                Rating = 4.3, // TODO: Calculate from reviews
                Active = vendor.Status == 1,
                JoinedAt = vendor.CreatedAt ?? DateTime.UtcNow,
                Stats = new VendorStatsDto
                {
                    TotalProducts = products.Count,
                    InStockProducts = inStockProducts,
                    TotalOrders = vendor.Orders.Count,
                    PendingOrders = vendor.Orders.Count(o => o.Status == "pending"),
                    TotalRevenue = vendor.Orders.Where(o => o.Status != "canceled").Sum(o => o.GrandTotal ?? 0),
                    CompletedOrders = completedOrders
                },
                RecentProducts = recentProducts,
                RecentOrders = recentOrders
            }
        };

        return Ok(response);
    }

    /// <summary>Update vendor status (activate/deactivate)</summary>
    /// <remarks>
    /// Toggle vendor active/inactive status using the status toggle switch.
    /// </remarks>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

        var vendor = await _db.Customers.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        vendor.Status = request.Active.Value ? 1 : 0;
        await _db.SaveChangesAsync();

        var action = request.Active.Value ? "activated" : "deactivated";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = vendor.Id, active = request.Active.Value },
            Message = $"{vendor.FirstName} {vendor.LastName} {action}"
        });
    }

    /// <summary>Get vendor's products</summary>
    [HttpGet("{id:int}/products")]
    public async Task<IActionResult> GetVendorProducts(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? filter = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var vendor = await _db.Customers.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var query = _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Where(p => p.ParentId == null)
            .AsNoTracking();

        // Search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(p =>
                p.Flats.Any(f => f.Name != null && f.Name.ToLower().Contains(searchLower)) ||
                p.Sku != null && p.Sku.ToLower().Contains(searchLower));
        }

        // Status filter
        if (!string.IsNullOrWhiteSpace(filter))
        {
            switch (filter.ToLower())
            {
                case "instock":
                    query = query.Where(p => p.Inventories.Any(i => i.Qty > 0));
                    break;
                case "outofstock":
                    query = query.Where(p => !p.Inventories.Any(i => i.Qty > 0));
                    break;
                case "active":
                    query = query.Where(p => p.Flats.Any(f => f.Status == true));
                    break;
                case "inactive":
                    query = query.Where(p => p.Flats.Any(f => f.Status != true));
                    break;
            }
        }

        var total = await query.CountAsync();
        var products = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var results = products.Select(p => new AdminProductDto
        {
            Id = p.Id,
            Sku = p.Sku ?? "",
            Name = p.Flats.FirstOrDefault()!.Name ?? "",
            Price = decimal.Parse(p.Flats.FirstOrDefault()!.Price?.ToString() ?? "0"),
            SpecialPrice = p.Flats.FirstOrDefault()!.SpecialPrice,
            CategoryName = p.Categories.FirstOrDefault() != null ? 
                p.Categories.FirstOrDefault()!.Translations.FirstOrDefault()?.Name ?? "" : "",
            VendorName = vendor.FirstName + " " + vendor.LastName,
            InStock = p.Inventories.Any(i => i.Qty > 0),
            StockQty = p.Inventories.Sum(i => i.Qty),
            AvgRating = 4.5, // TODO: Calculate from reviews
            ReviewsCount = 0, // TODO: Count reviews
            Active = p.Flats.FirstOrDefault()!.Status ?? false
        }).ToList();

        return Ok(new VendorProductListResponse
        {
            Data = results,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            }
        });
    }

    /// <summary>Update product status within vendor</summary>
    [HttpPatch("{id:int}/products/{productId:int}/status")]
    public async Task<IActionResult> UpdateProductStatus(
        int id,
        int productId,
        [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

        var product = await _db.Products
            .Include(p => p.Flats)
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
            return NotFound(new { message = "Product not found" });

        var flat = product.Flats.FirstOrDefault();
        if (flat != null)
        {
            flat.Status = request.Active.Value;
            await _db.SaveChangesAsync();
        }

        var action = request.Active.Value ? "activated" : "deactivated";
        var productName = flat?.Name ?? "Product";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = product.Id, active = request.Active.Value },
            Message = $"\"{productName}\" {action}"
        });
    }

    /// <summary>Get all categories (shared across vendors)</summary>
    [HttpGet("{id:int}/categories")]
    public async Task<IActionResult> GetCategories(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var vendor = await _db.Customers.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var categories = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Products)
            .AsNoTracking()
            .ToListAsync();

        var results = categories.Select(c => new AdminCategoryDto
        {
            Id = c.Id,
            Name = c.Translations.FirstOrDefault()?.Name ?? "",
            Slug = c.Translations.FirstOrDefault()?.Slug ?? "",
            Description = c.Translations.FirstOrDefault()?.Description ?? "",
            Active = c.Status,
            VendorCount = 1, // TODO: Calculate vendor count
            ProductCount = c.Products.Count
        }).ToList();

        return Ok(new VendorCategoryListResponse { Data = results });
    }

    /// <summary>Get vendor orders</summary>
    [HttpGet("{id:int}/orders")]
    public async Task<IActionResult> GetVendorOrders(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var vendor = await _db.Customers.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var query = _db.Orders
            .Where(o => o.CustomerId == id)
            .AsNoTracking();

        // Status filter (includes cancelled for vendor orders)
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(o => o.Status == status.ToLower());
        }

        var total = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(o => new OrderListDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                ItemsCount = o.TotalItemCount ?? 0,
                CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
                CustomerPhone = o.CustomerEmail ?? "", // Using email as placeholder
                VendorName = vendor.FirstName + " " + vendor.LastName,
                PaymentMethod = "", // TODO: Get from payment
                DeliveryAddress = "" // TODO: Get from address
            })
            .ToListAsync();

        return Ok(new VendorOrderListResponse
        {
            Data = orders,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            }
        });
    }

    /// <summary>Get vendor transactions</summary>
    [HttpGet("{id:int}/transactions")]
    public async Task<IActionResult> GetVendorTransactions(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var vendor = await _db.Customers.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        // TODO: Implement actual transaction table queries
        // This is a placeholder structure for now
        var allTransactions = new List<VendorTransactionDto>();
        var total = allTransactions.Count;

        var transactions = allTransactions
            .OrderByDescending(t => t.Date)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToList();

        var settled = allTransactions.Where(t => t.Status == "settled").Sum(t => t.Amount);
        var pending = allTransactions.Where(t => t.Status == "pending").Sum(t => t.Amount);
        var refunds = allTransactions.Where(t => t.Type == "debit").Sum(t => t.Amount);

        return Ok(new VendorTransactionListResponse
        {
            Data = transactions,
            Meta = new PaginationMeta
            {
                Total = total,
                CurrentPage = page,
                LastPage = (total + limit - 1) / limit,
                PerPage = limit
            },
            Summary = new VendorTransactionSummary
            {
                TotalSettled = settled,
                TotalPending = pending,
                TotalRefunds = refunds
            }
        });
    }
}
