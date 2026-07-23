using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Models.Catalog;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin vendor management controller.
/// Routes: /api/v1/admin/vendors
///
/// Vendor identity is the `vendors` table (see Models/Catalog/Vendor.cs),
/// backfilled by VendorCatalogSeeder from the free-text seller name in each
/// product's `additional` JSON (`vendor_en`/`vendor_te` — the same field the
/// storefront's "Sold by" label reads via ProductService.GetProductVendor).
/// There is no FK from products/orders to a vendor — matching is by name,
/// computed in-memory per request (cheap at current catalog scale; revisit
/// with a materialized/cached aggregate if the catalog grows much larger).
/// </summary>
[Route("api/v1/admin/vendors")]
[Tags("Admin — Vendors")]
public class AdminVendorsController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly VendorAggregationService _aggregation;
    private readonly ProductService _productService;
    private readonly IConfiguration _config;

    public AdminVendorsController(DOSDbContext db, IConfiguration config, VendorAggregationService aggregation, ProductService productService) : base(config)
    {
        _db = db;
        _aggregation = aggregation;
        _productService = productService;
        _config = config;
    }

    /// <summary>List all vendors with search and filter</summary>
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

        var query = _db.Vendors.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(v => v.Name.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var isActive = status.Equals("active", StringComparison.OrdinalIgnoreCase);
            query = query.Where(v => v.Active == isActive);
        }

        var total = await query.CountAsync();
        var vendors = await query
            .OrderBy(v => v.Name)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var aggregates = await _aggregation.BuildVendorAggregatesAsync(productVendorMap);

        var results = vendors.Select(v =>
        {
            aggregates.TryGetValue(v.Name, out var agg);
            return new VendorDto
            {
                Id = v.Id,
                Name = v.Name,
                Email = "",
                Phone = "",
                City = "",
                Products = agg?.Products ?? 0,
                Orders = agg?.Orders ?? 0,
                Revenue = agg?.Revenue ?? 0,
                Rating = 4.3,
                Active = v.Active,
                JoinedAt = v.CreatedAt ?? DateTime.UtcNow
            };
        }).ToList();

        return Ok(new VendorListResponse
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

    /// <summary>Get vendor detail overview</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var vendor = await _db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorProductIds = productVendorMap
            .Where(kv => string.Equals(kv.Value, vendor.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        var aggregates = await _aggregation.BuildVendorAggregatesAsync(productVendorMap);
        aggregates.TryGetValue(vendor.Name, out var agg);

        var recentProducts = await _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Where(p => vendorProductIds.Contains(p.Id))
            .OrderByDescending(p => p.CreatedAt)
            .Take(3)
            .AsNoTracking()
            .ToListAsync();

        var vendorOrderItems = await _db.OrderItems
            .Where(i => i.ProductId != null && vendorProductIds.Contains(i.ProductId.Value) && i.OrderId != null)
            .Select(i => i.OrderId!.Value)
            .Distinct()
            .ToListAsync();

        var recentOrdersRaw = await _db.Orders
            .Where(o => vendorOrderItems.Contains(o.Id))
            .OrderByDescending(o => o.CreatedAt)
            .Take(4)
            .AsNoTracking()
            .ToListAsync();

        var pendingOrders = await _db.Orders
            .CountAsync(o => vendorOrderItems.Contains(o.Id) && o.Status == "pending");
        var completedOrders = await _db.Orders
            .CountAsync(o => vendorOrderItems.Contains(o.Id) && o.Status == "completed");
        var inStockProducts = await _db.Products
            .CountAsync(p => vendorProductIds.Contains(p.Id) && p.Inventories.Any(i => i.Qty > 0));

        var response = new VendorDetailResponse
        {
            Data = new VendorDetailDto
            {
                Id = vendor.Id,
                Name = vendor.Name,
                Email = "",
                Phone = "",
                City = "",
                Rating = 4.3,
                Active = vendor.Active,
                JoinedAt = vendor.CreatedAt ?? DateTime.UtcNow,
                Stats = new VendorStatsDto
                {
                    TotalProducts = agg?.Products ?? 0,
                    InStockProducts = inStockProducts,
                    TotalOrders = agg?.Orders ?? 0,
                    PendingOrders = pendingOrders,
                    TotalRevenue = agg?.Revenue ?? 0,
                    CompletedOrders = completedOrders
                },
                RecentProducts = recentProducts.Select(p => new VendorProductDto
                {
                    Id = p.Id,
                    Name = p.Flats.FirstOrDefault()?.Name ?? "",
                    Price = decimal.Parse(p.Flats.FirstOrDefault()?.Price?.ToString() ?? "0"),
                    SpecialPrice = p.Flats.FirstOrDefault()?.SpecialPrice,
                    CategoryName = p.Categories.FirstOrDefault()?.Translations.FirstOrDefault()?.Name ?? "",
                    InStock = p.Inventories.Any(i => i.Qty > 0)
                }).ToList(),
                RecentOrders = recentOrdersRaw.Select(o => new RecentOrderDto
                {
                    Id = o.Id,
                    IncrementId = o.IncrementId ?? "",
                    Status = o.Status ?? "pending",
                    GrandTotal = o.GrandTotal ?? 0,
                    CustomerName = $"{o.CustomerFirstName} {o.CustomerLastName}".Trim(),
                    VendorName = vendor.Name
                }).ToList()
            }
        };

        return Ok(response);
    }

    /// <summary>Update vendor status (activate/deactivate)</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        vendor.Active = request.Active.Value;
        vendor.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var action = request.Active.Value ? "activated" : "deactivated";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = vendor.Id, active = request.Active.Value },
            Message = $"{vendor.Name} {action}"
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

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorProductIds = productVendorMap
            .Where(kv => string.Equals(kv.Value, vendor.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        var query = _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Include(p => p.Images)
            .Include(p => p.Children)
            .Where(p => vendorProductIds.Contains(p.Id))
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(p =>
                p.Flats.Any(f => f.Name != null && f.Name.ToLower().Contains(searchLower)) ||
                (p.Sku != null && p.Sku.ToLower().Contains(searchLower)));
        }

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

        var results = products.Select(p =>
        {
            var image = p.Images.OrderBy(i => i.Position).FirstOrDefault();
            return new AdminProductDto
            {
                Id = p.Id,
                Sku = p.Sku ?? "",
                Name = p.Flats.FirstOrDefault()?.Name ?? "",
                Price = decimal.Parse(p.Flats.FirstOrDefault()?.Price?.ToString() ?? "0"),
                SpecialPrice = p.Flats.FirstOrDefault()?.SpecialPrice,
                CategoryName = p.Categories.FirstOrDefault()?.Translations.FirstOrDefault()?.Name ?? "",
                CategoryId = p.Categories.FirstOrDefault()?.Id,
                VendorName = vendor.Name,
                InStock = p.Inventories.Any(i => i.Qty > 0),
                StockQty = p.Inventories.Sum(i => i.Qty),
                AvgRating = 0,
                ReviewsCount = 0,
                Active = p.Flats.Any(f => f.Status == true),
                ImageId = image?.Id,
                ImageUrl = _productService.GetBaseImageUrl(p),
                VariantCount = p.Children.Count,
                ShortDescription = p.Flats.FirstOrDefault()?.ShortDescription,
                Description = p.Flats.FirstOrDefault()?.Description
            };
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

        // Update ALL locale flats — a single-flat toggle leaves the product
        // still visible via other locales (see AdminGlobalProductsController
        // for the same fix and full explanation).
        foreach (var f in product.Flats)
        {
            f.Status = request.Active.Value;
        }
        await _db.SaveChangesAsync();

        var action = request.Active.Value ? "activated" : "deactivated";
        var productName = product.Flats.FirstOrDefault(f => f.Locale == "en")?.Name
            ?? product.Flats.FirstOrDefault()?.Name ?? "Product";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = product.Id, active = request.Active.Value },
            Message = $"\"{productName}\" {action}"
        });
    }

    /// <summary>Get all categories (shared across vendors, per spec — not vendor-scoped)</summary>
    [HttpGet("{id:int}/categories")]
    public async Task<IActionResult> GetCategories(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        // Same tree (id 1 = "Root", every real category hangs off it) and
        // hierarchy/image mapping as AdminGlobalCategoriesController.List() —
        // kept in sync so this tab has full parity with the main Categories screen.
        var categories = await _db.Categories
            .Include(c => c.Translations)
            .Include(c => c.Products)
            .AsNoTracking()
            .Where(c => c.Id != AdminGlobalCategoriesController.RootCategoryId)
            .OrderBy(c => c.Lft)
            .ToListAsync();

        var namesById = categories.ToDictionary(c => c.Id, c => c.Translations.FirstOrDefault()?.Name ?? "");

        var results = categories.Select(c => new AdminCategoryDto
        {
            Id = c.Id,
            Name = c.Translations.FirstOrDefault()?.Name ?? "",
            Slug = c.Translations.FirstOrDefault()?.Slug ?? "",
            Description = c.Translations.FirstOrDefault()?.Description ?? "",
            Active = c.Status,
            VendorCount = 1, // TODO: Calculate real vendor count once per-category vendor linkage exists
            ProductCount = c.Products.Count,
            ParentId = c.ParentId == AdminGlobalCategoriesController.RootCategoryId ? null : c.ParentId,
            ParentName = (c.ParentId.HasValue && c.ParentId != AdminGlobalCategoriesController.RootCategoryId && namesById.TryGetValue(c.ParentId.Value, out var pn)) ? pn : null,
            LogoUrl = ResolveAssetUrl(c.LogoPath),
            BannerUrl = ResolveAssetUrl(c.BannerPath)
        }).ToList();

        return Ok(new VendorCategoryListResponse { Data = results });
    }

    // Older categories carry a legacy Bagisto-relative logo/banner path; see
    // AdminGlobalCategoriesController.ResolveAssetUrl / CategoryController.ResolveAssetUrl.
    private string? ResolveAssetUrl(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http://") || path.StartsWith("https://")) return path;
        var baseUrl = (_config["App:BaseUrl"] ?? "http://192.168.0.116:8000").TrimEnd('/');
        return $"{baseUrl}/storage/{path}";
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

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorProductIds = productVendorMap
            .Where(kv => string.Equals(kv.Value, vendor.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        var vendorOrderIds = await _db.OrderItems
            .Where(i => i.ProductId != null && vendorProductIds.Contains(i.ProductId.Value) && i.OrderId != null)
            .Select(i => i.OrderId!.Value)
            .Distinct()
            .ToListAsync();

        var query = _db.Orders
            .Where(o => vendorOrderIds.Contains(o.Id))
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
            .Select(o => new OrderListDto
            {
                Id = o.Id,
                IncrementId = o.IncrementId ?? "",
                PlacedAt = o.CreatedAt ?? DateTime.UtcNow,
                Status = o.Status ?? "pending",
                GrandTotal = o.GrandTotal ?? 0,
                ItemsCount = o.TotalItemCount ?? 0,
                CustomerName = o.CustomerFirstName + " " + o.CustomerLastName,
                CustomerPhone = o.CustomerEmail ?? "",
                VendorName = vendor.Name,
                PaymentMethod = "",
                DeliveryAddress = ""
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

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        // No settlement/transaction system exists yet — same known gap as
        // before, just now scoped to a real vendor instead of a fake one.
        return Ok(new VendorTransactionListResponse
        {
            Data = new List<VendorTransactionDto>(),
            Meta = new PaginationMeta { Total = 0, CurrentPage = page, LastPage = 1, PerPage = limit },
            Summary = new VendorTransactionSummary { TotalSettled = 0, TotalPending = 0, TotalRefunds = 0 }
        });
    }
}
