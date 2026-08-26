using System.Text.Json;
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

    public AdminVendorsController(DOSDbContext db, IConfiguration config, VendorAggregationService aggregation, ProductService productService) : base(db, config)
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
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();
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
                NameTe = v.NameTe,
                Email = "",
                Phone = v.Phone ?? "",
                Address = v.Address,
                City = "",
                Products = agg?.Products ?? 0,
                Orders = agg?.Orders ?? 0,
                Revenue = agg?.Revenue ?? 0,
                Rating = 4.3,
                Active = v.Active,
                SortOrder = v.SortOrder,
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

    /// <summary>Lightweight vendor list for pickers/dropdowns (no aggregates)</summary>
    /// <remarks>
    /// Just `{id, name, active}` for every vendor, ordered by name — used by the
    /// admin product form's vendor picker. Deliberately skips
    /// <see cref="VendorAggregationService"/>'s per-vendor product/order/revenue
    /// computation (see <see cref="List"/>), which is unnecessary work for a
    /// dropdown and would otherwise run on every product-form open.
    /// </remarks>
    /// <param name="activeOnly">When true (default), hides deactivated vendors.</param>
    /// <param name="categoryId">
    /// When given, restricts to vendors that have at least one product in this
    /// category or any of its subcategories — used by the admin Products list's
    /// Vendor filter so picking a category first narrows the Vendor picker to
    /// vendors actually selling in it, instead of showing every vendor.
    /// </param>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] bool activeOnly = true, [FromQuery] int? categoryId = null)
    {
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();

        var query = _db.Vendors.AsNoTracking().AsQueryable();
        if (activeOnly) query = query.Where(v => v.Active);

        var vendors = await query.OrderBy(v => v.Name).ToListAsync();

        if (categoryId.HasValue)
        {
            var subtreeIds = await _productService.GetSubtreeCategoryIdsAsync(categoryId.Value);
            var productsInCategory = await _db.Products
                .Where(p => p.ParentId == null
                    && p.Additional != null && p.Additional != ""
                    && p.Categories.Any(c => subtreeIds.Contains(c.Id)))
                .Select(p => p.Additional)
                .ToListAsync();

            var namesInCategory = productsInCategory
                .Select(json => ProductService.ExtractVendorName(json, "en")?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            vendors = vendors.Where(v => namesInCategory.Contains(v.Name)).ToList();
        }

        var result = vendors.Select(v => new { v.Id, v.Name, v.NameTe, v.Active }).ToList();
        return Ok(new { data = result });
    }

    /// <summary>Create a new vendor</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVendorRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "name is required" });

        var name = request.Name.Trim();
        if (await _db.Vendors.AnyAsync(v => v.Name == name))
            return Conflict(new { message = $"A vendor named '{name}' already exists" });

        var phone = request.Phone?.Trim();
        if (!string.IsNullOrEmpty(phone) && !IsValidPhone(phone))
            return BadRequest(new { message = "phone must be a 10-digit mobile number" });

        var now = DateTime.UtcNow;
        var vendor = new Models.Catalog.Vendor
        {
            Name = name,
            NameTe = !string.IsNullOrWhiteSpace(request.NameTe) ? request.NameTe!.Trim() : name,
            Phone = string.IsNullOrEmpty(phone) ? null : phone,
            Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
            Active = request.Active,
            SortOrder = request.SortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Vendors.Add(vendor);
        await _db.SaveChangesAsync();

        return Ok(new CreatedResponse<VendorDto>
        {
            Data = new VendorDto
            {
                Id = vendor.Id,
                Name = vendor.Name,
                NameTe = vendor.NameTe,
                Phone = vendor.Phone ?? "",
                Address = vendor.Address,
                Active = vendor.Active,
                SortOrder = vendor.SortOrder,
                JoinedAt = vendor.CreatedAt ?? now
            },
            Message = $"\"{name}\" added"
        });
    }

    private static bool IsValidPhone(string phone) =>
        phone.Length == 10 && phone.All(char.IsDigit);

    /// <summary>Update a vendor's name(s) and status</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateVendorRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "name is required" });

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var name = request.Name.Trim();
        if (await _db.Vendors.AnyAsync(v => v.Name == name && v.Id != id))
            return Conflict(new { message = $"A vendor named '{name}' already exists" });

        var phone = request.Phone?.Trim();
        if (!string.IsNullOrEmpty(phone) && !IsValidPhone(phone))
            return BadRequest(new { message = "phone must be a 10-digit mobile number" });

        var oldName = vendor.Name;
        vendor.Name = name;
        vendor.NameTe = !string.IsNullOrWhiteSpace(request.NameTe) ? request.NameTe!.Trim() : name;
        vendor.Phone = string.IsNullOrEmpty(phone) ? null : phone;
        vendor.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        vendor.Active = request.Active;
        if (request.SortOrder.HasValue) vendor.SortOrder = request.SortOrder.Value;
        vendor.UpdatedAt = DateTime.UtcNow;

        // Products carry no vendor FK — they're matched to this vendor by the
        // free-text name in their own Additional JSON (see
        // VendorAggregationService / ProductService.ExtractVendorName). An
        // actual rename must also update every matching product's Additional,
        // or they'd silently fall out of this vendor's product list the
        // moment the name changes.
        if (!string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
        {
            var candidates = await _db.Products
                .Where(p => p.Additional != null && p.Additional != "")
                .ToListAsync();
            foreach (var p in candidates)
            {
                if (string.Equals(ProductService.ExtractVendorName(p.Additional, "en"), oldName, StringComparison.OrdinalIgnoreCase))
                {
                    p.Additional = JsonSerializer.Serialize(new { vendor_en = vendor.Name, vendor_te = vendor.NameTe });
                }
            }
        }

        await _db.SaveChangesAsync();

        return Ok(new UpdatedResponse<VendorDto>
        {
            Data = new VendorDto
            {
                Id = vendor.Id,
                Name = vendor.Name,
                NameTe = vendor.NameTe,
                Phone = vendor.Phone ?? "",
                Address = vendor.Address,
                Active = vendor.Active,
                SortOrder = vendor.SortOrder,
                JoinedAt = vendor.CreatedAt ?? DateTime.UtcNow
            },
            Message = $"\"{name}\" updated"
        });
    }

    /// <summary>Get vendor detail overview</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();

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
            .Include(p => p.Inventories)
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
                NameTe = vendor.NameTe,
                Email = "",
                Phone = vendor.Phone ?? "",
                Address = vendor.Address,
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
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
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
    /// <param name="categoryId">Restrict to this category AND all of its subcategories</param>
    [HttpGet("{id:int}/products")]
    public async Task<IActionResult> GetVendorProducts(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? filter = null,
        [FromQuery] int? categoryId = null)
    {
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();
        if (page < 1) page = 1;
        // The vendor detail screen's Products tab has no "load more"/infinite
        // scroll — it does one fetch and renders everything, and since
        // ReorderProducts/the category-group split need the vendor's *whole*
        // catalog to make sense (grouping only within what's actually
        // loaded), this needs a much higher ceiling than a normal paginated
        // list. 2000 comfortably covers any single vendor's catalog at
        // current scale.
        if (limit is < 1 or > 2000) limit = 20;

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorProductIds = productVendorMap
            .Where(kv => string.Equals(kv.Value, vendor.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        var query = _db.Products
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

        // Category filter — includes the whole sub-tree (see AdminGlobalProductsController.List).
        if (categoryId.HasValue)
        {
            var subtreeIds = await _productService.GetSubtreeCategoryIdsAsync(categoryId.Value);
            query = query.Where(p => p.Categories.Any(c => subtreeIds.Contains(c.Id)));
        }

        var total = await query.CountAsync();

        // Within-vendor product order — an admin-set rank (see
        // ReorderProducts below) that ranks this vendor's own products
        // against each other; unset products (no row here) fall back to the
        // original newest-first order, same tie-break as before. Matches the
        // two-pass shape in CategoryController.QueryCategoryProductsAsync,
        // which applies this same rank on the storefront.
        var candidates = await query
            .Select(p => new
            {
                p.Id,
                p.CreatedAt,
                CategoryId = p.Categories.FirstOrDefault() != null ? (int?)p.Categories.FirstOrDefault()!.Id : null
            })
            .ToListAsync();
        var sortOrderByProductId = await _db.ProductVendorSortOrders.AsNoTracking()
            .Where(s => s.VendorId == id)
            .ToDictionaryAsync(s => s.ProductId, s => s.SortOrder);

        int Rank(int productId) => sortOrderByProductId.TryGetValue(productId, out var r) ? r : int.MaxValue;

        // Group by main (top-level) category so an admin browsing "All
        // categories" sees this vendor's Services products, Store products,
        // etc. as separate blocks in a stable order — instead of one flat
        // list where products from different categories interleave purely
        // by CreatedAt, making within-vendor rank restart at a confusing
        // offset per category (see the "Cornerstone" report this fixes).
        // Only meaningful when NOT already scoped to one category (which is
        // already a single group).
        var mainCategoryByCategoryId = new Dictionary<int, int>();
        var mainCategoryDisplayOrder = new Dictionary<int, int>();
        if (!categoryId.HasValue)
        {
            var directCategoryIds = candidates.Where(c => c.CategoryId.HasValue).Select(c => c.CategoryId!.Value);
            mainCategoryByCategoryId = await _productService.ResolveMainCategoryIdsAsync(directCategoryIds);
            mainCategoryDisplayOrder = await _productService.GetMainCategoryDisplayOrderAsync();
        }

        int GroupRank(int? directCategoryId)
        {
            if (categoryId.HasValue || !directCategoryId.HasValue) return 0;
            var mainId = mainCategoryByCategoryId.GetValueOrDefault(directCategoryId.Value, directCategoryId.Value);
            return mainCategoryDisplayOrder.TryGetValue(mainId, out var order) ? order : int.MaxValue;
        }

        var pageIds = candidates
            .OrderBy(c => GroupRank(c.CategoryId))
            .ThenBy(c => Rank(c.Id))
            .ThenByDescending(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(c => c.Id)
            .ToList();

        // Projection instead of .Include()-ing 5 collections just to read a
        // first/sum/count out of each — see AdminGlobalProductsController.List
        // for why (cartesian-multiplied join, fine locally, very slow against
        // real prod latency).
        var rows = await _db.Products
            .Where(p => pageIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Sku,
                Name = p.Flats.FirstOrDefault()!.Name,
                Price = p.Flats.FirstOrDefault()!.Price,
                SpecialPrice = p.Flats.FirstOrDefault()!.SpecialPrice,
                CategoryName = p.Categories.FirstOrDefault() != null
                    ? p.Categories.FirstOrDefault()!.Translations.FirstOrDefault()!.Name
                    : null,
                CategoryId = p.Categories.FirstOrDefault() != null ? (int?)p.Categories.FirstOrDefault()!.Id : null,
                InStock = p.Inventories.Any(i => i.Qty > 0),
                StockQty = p.Inventories.Sum(i => (int?)i.Qty) ?? 0,
                Active = p.Flats.Any(f => f.Status == true),
                ImageId = p.Images.OrderBy(i => i.Position).Select(i => (int?)i.Id).FirstOrDefault(),
                ImagePath = p.Images.OrderBy(i => i.Position).Select(i => i.Path).FirstOrDefault(),
                VariantCount = p.Children.Count(),
                ShortDescription = p.Flats.FirstOrDefault()!.ShortDescription,
                Description = p.Flats.FirstOrDefault()!.Description,
                NameTe = p.Flats.FirstOrDefault(f => f.Locale == "te")!.Name,
                ShortDescriptionTe = p.Flats.FirstOrDefault(f => f.Locale == "te")!.ShortDescription,
                DescriptionTe = p.Flats.FirstOrDefault(f => f.Locale == "te")!.Description
            })
            .AsNoTracking()
            .ToListAsync();

        // EF's `WHERE Id IN (...)` doesn't preserve pageIds' order, so
        // re-order the hydrated rows to match it (same fix as
        // CategoryController.QueryCategoryProductsAsync).
        var rowById = rows.ToDictionary(r => r.Id);
        var orderedRows = pageIds.Where(rowById.ContainsKey).Select(pid => rowById[pid]).ToList();

        var mainCategoryIds = mainCategoryByCategoryId.Values.Distinct().ToList();
        var mainCategoryNames = mainCategoryIds.Count > 0
            ? await _db.Categories.AsNoTracking()
                .Where(c => mainCategoryIds.Contains(c.Id))
                .Select(c => new { c.Id, Name = c.Translations.FirstOrDefault()!.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "")
            : new Dictionary<int, string>();

        int? MainCategoryIdFor(int? directCategoryId) =>
            directCategoryId.HasValue && mainCategoryByCategoryId.TryGetValue(directCategoryId.Value, out var mcid) ? mcid : null;

        var results = orderedRows.Select(r => new AdminProductDto
        {
            Id = r.Id,
            Sku = r.Sku ?? "",
            Name = r.Name ?? "",
            Price = r.Price ?? 0,
            SpecialPrice = r.SpecialPrice,
            CategoryName = r.CategoryName ?? "",
            CategoryId = r.CategoryId,
            VendorName = vendor.Name,
            InStock = r.InStock,
            StockQty = r.StockQty,
            AvgRating = 0,
            ReviewsCount = 0,
            Active = r.Active,
            ImageId = r.ImageId,
            ImageUrl = _productService.GetImageUrl(r.ImagePath),
            VariantCount = r.VariantCount,
            ShortDescription = r.ShortDescription,
            Description = r.Description,
            NameTe = r.NameTe,
            ShortDescriptionTe = r.ShortDescriptionTe,
            DescriptionTe = r.DescriptionTe,
            VendorSortOrder = sortOrderByProductId.TryGetValue(r.Id, out var so) ? so : 0,
            MainCategoryId = MainCategoryIdFor(r.CategoryId),
            MainCategoryName = MainCategoryIdFor(r.CategoryId) is int mcid ? mainCategoryNames.GetValueOrDefault(mcid, "") : null
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

    /// <summary>
    /// Reorders this vendor's own products against each other — the order
    /// used within this vendor's block on category/home listings (see
    /// CategoryController.QueryCategoryProductsAsync). Distinct from
    /// Vendor.SortOrder (AdminVendorsController.Update), which ranks whole
    /// vendor blocks against other vendors, not products within one.
    /// Body: [{ "productId": 12, "sortOrder": 0 }, { "productId": 8, "sortOrder": 1 }].
    /// </summary>
    [HttpPatch("{id:int}/products/reorder")]
    public async Task<IActionResult> ReorderProducts(int id, [FromBody] List<ProductOrderItem> items)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
        if (items == null || items.Count == 0)
            return BadRequest(new { message = "No items provided." });

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        // Guard against reordering another vendor's product into this
        // vendor's rank table — products are matched by free-text name, not
        // FK, so nothing else stops a stale client-side list from doing that.
        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorProductIds = productVendorMap
            .Where(kv => string.Equals(kv.Value, vendor.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        var requestedIds = items.Select(i => i.ProductId).ToList();
        var invalidIds = requestedIds.Where(pid => !vendorProductIds.Contains(pid)).ToList();
        if (invalidIds.Count > 0)
            return BadRequest(new { message = $"Product(s) {string.Join(", ", invalidIds)} don't belong to this vendor." });

        var existing = await _db.ProductVendorSortOrders
            .Where(s => requestedIds.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId);

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.ProductId, out var row))
            {
                row.SortOrder = item.SortOrder;
                row.UpdatedAt = now;
            }
            else
            {
                _db.ProductVendorSortOrders.Add(new ProductVendorSortOrder
                {
                    ProductId = item.ProductId,
                    VendorId = id,
                    SortOrder = item.SortOrder,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        await _db.SaveChangesAsync();

        return Ok(new { message = "Product order updated." });
    }

    public record ProductOrderItem(int ProductId, int SortOrder);

    /// <summary>Update product status within vendor</summary>
    [HttpPatch("{id:int}/products/{productId:int}/status")]
    public async Task<IActionResult> UpdateProductStatus(
        int id,
        int productId,
        [FromBody] UpdateStatusRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
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

    /// <summary>Get the categories this vendor's own products are actually filed
    /// under — unlike the platform-wide Global Categories tree, this is scoped
    /// to the vendor. There's no vendor↔category linkage table (vendor identity
    /// is name-matched from product data, per the class remarks above), so
    /// "belongs to this vendor" is computed as: categories holding at least one
    /// of the vendor's own products, plus each such category's ancestor chain
    /// (so e.g. "Build Store" still shows as a parent even if the vendor only
    /// has products in its "Bricks & Blocks" child, not directly in "Build
    /// Store" itself) — otherwise the tree would render as disconnected
    /// fragments. ProductCount is scoped to this vendor's products too, not
    /// the platform total. Read-only: category status/name/etc. are managed
    /// globally via AdminGlobalCategoriesController, not per-vendor.</summary>
    [HttpGet("{id:int}/categories")]
    public async Task<IActionResult> GetCategories(int id)
    {
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();

        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null)
            return NotFound(new { message = "Vendor not found" });

        var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
        var vendorProductIds = productVendorMap
            .Where(kv => string.Equals(kv.Value, vendor.Name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        // Same tree (id 1 = "Root", every real category hangs off it) and
        // hierarchy/image mapping as AdminGlobalCategoriesController.List().
        var allCategories = await _db.Categories
            .Include(c => c.Translations)
            .AsNoTracking()
            .Where(c => c.Id != AdminGlobalCategoriesController.RootCategoryId)
            .OrderBy(c => c.Lft)
            .ToListAsync();

        // Product counts via a separate GROUP BY instead of .Include(c => c.Products)
        // — see AdminGlobalCategoriesController.List() for why (cartesian-joined
        // every category against every one of its products just for a count,
        // fine locally but 30+ seconds then a failure against real prod latency).
        // Scoped to this vendor's products only, unlike the global endpoint.
        var vendorProductCounts = await _db.Products
            .Where(p => p.ParentId == null && vendorProductIds.Contains(p.Id))
            .SelectMany(p => p.Categories.Select(c => c.Id))
            .GroupBy(catId => catId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        // Walk each directly-tagged category up to Root so ancestors with none
        // of the vendor's products directly (but a qualifying descendant) are
        // still included — memoized via includedIds.Add's return value so a
        // shared ancestor chain is only walked once.
        var parentById = allCategories.ToDictionary(c => c.Id, c => c.ParentId);
        var includedIds = new HashSet<int>();
        foreach (var catId in vendorProductCounts.Keys)
        {
            int? current = catId;
            while (current.HasValue && current != AdminGlobalCategoriesController.RootCategoryId && includedIds.Add(current.Value))
            {
                parentById.TryGetValue(current.Value, out var parent);
                current = parent;
            }
        }

        var categories = allCategories.Where(c => includedIds.Contains(c.Id)).ToList();
        var namesById = categories.ToDictionary(c => c.Id, c => c.Translations.FirstOrDefault()?.Name ?? "");

        var results = categories.Select(c => new AdminCategoryDto
        {
            Id = c.Id,
            Name = c.Translations.FirstOrDefault()?.Name ?? "",
            Slug = c.Translations.FirstOrDefault()?.Slug ?? "",
            Description = c.Translations.FirstOrDefault()?.Description ?? "",
            Active = c.Status,
            VendorCount = 1, // TODO: Calculate real vendor count once per-category vendor linkage exists
            ProductCount = vendorProductCounts.GetValueOrDefault(c.Id, 0),
            ParentId = c.ParentId == AdminGlobalCategoriesController.RootCategoryId ? null : c.ParentId,
            ParentName = (c.ParentId.HasValue && c.ParentId != AdminGlobalCategoriesController.RootCategoryId && namesById.TryGetValue(c.ParentId.Value, out var pn)) ? pn : null,
            LogoUrl = ResolveAssetUrl(c.LogoPath),
            BannerUrl = ResolveAssetUrl(c.BannerPath),
            NameTe = c.Translations.FirstOrDefault(t => t.Locale == "te")?.Name,
            DescriptionTe = c.Translations.FirstOrDefault(t => t.Locale == "te")?.Description
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
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();
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
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();
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

    // ─── Category priority overrides ───────────────────────────────────
    //
    // Vendor.SortOrder ranks a vendor's whole product block against every
    // other vendor's, the same way on every category/home listing. These
    // rows let the admin override that per category instead — e.g. Vendor A
    // ranks first in "Vegetables" while Vendor B ranks first in "Dairy" —
    // without changing Vendor.SortOrder's flat, cart-wide-equivalent value.
    // See VendorCategorySortOrder and
    // CategoryController.QueryCategoryProductsAsync for how it resolves.

    public record CategoryPriorityRequest(int CategoryId, int SortOrder, bool Active = true);

    private static string? ValidatePriority(int sortOrder) =>
        sortOrder < 1 ? "Priority must be a positive number." : null;

    /// <summary>List a vendor's category priority overrides.</summary>
    [HttpGet("{vendorId:int}/category-priority")]
    public async Task<IActionResult> ListCategoryPriorities(int vendorId)
    {
        if (!await HasPermissionAsync("vendors")) return AdminUnauthorized();
        if (!await _db.Vendors.AnyAsync(v => v.Id == vendorId))
            return NotFound(new { success = false, message = "Vendor not found." });

        var overrides = await _db.VendorCategorySortOrders
            .AsNoTracking()
            .Where(o => o.VendorId == vendorId)
            .OrderBy(o => o.Id)
            .ToListAsync();
        return Ok(new { success = true, data = await ToPriorityDtosAsync(overrides) });
    }

    /// <summary>Add a category priority override for a vendor.</summary>
    [HttpPost("{vendorId:int}/category-priority")]
    public async Task<IActionResult> CreateCategoryPriority(int vendorId, [FromBody] CategoryPriorityRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
        if (!await _db.Vendors.AnyAsync(v => v.Id == vendorId))
            return NotFound(new { success = false, message = "Vendor not found." });
        var priorityError = ValidatePriority(request.SortOrder);
        if (priorityError != null) return BadRequest(new { success = false, message = priorityError });
        if (!await _db.Categories.AnyAsync(c => c.Id == request.CategoryId))
            return BadRequest(new { success = false, message = "Selected category was not found." });
        if (await _db.VendorCategorySortOrders.AnyAsync(o => o.VendorId == vendorId && o.CategoryId == request.CategoryId))
            return BadRequest(new { success = false, message = "This vendor already has a priority set for this category." });

        var now = DateTime.UtcNow;
        var entity = new VendorCategorySortOrder
        {
            VendorId = vendorId,
            CategoryId = request.CategoryId,
            SortOrder = request.SortOrder,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.VendorCategorySortOrders.Add(entity);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Category priority added.", data = (await ToPriorityDtosAsync(new List<VendorCategorySortOrder> { entity }))[0] });
    }

    public record BulkCategoryPriorityRequest(List<int> CategoryIds, int SortOrder, bool Active = true);

    /// <summary>Give a vendor the same priority across several categories at once</summary>
    /// <remarks>
    /// Categories that already have a priority set for this vendor are skipped (not
    /// overwritten) rather than failing the whole batch — the response's `skipped` list
    /// names which ones, so the admin can edit those individually instead.
    /// </remarks>
    [HttpPost("{vendorId:int}/category-priority/bulk")]
    public async Task<IActionResult> CreateCategoryPrioritiesBulk(int vendorId, [FromBody] BulkCategoryPriorityRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");
        if (!await _db.Vendors.AnyAsync(v => v.Id == vendorId))
            return NotFound(new { success = false, message = "Vendor not found." });
        var priorityError = ValidatePriority(request.SortOrder);
        if (priorityError != null) return BadRequest(new { success = false, message = priorityError });

        var categoryIds = (request.CategoryIds ?? new List<int>()).Distinct().ToList();
        if (categoryIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one category." });

        var foundIds = await _db.Categories.Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missing = categoryIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Category id(s) not found: {string.Join(", ", missing)}." });

        var existingCategoryIds = await _db.VendorCategorySortOrders
            .Where(o => o.VendorId == vendorId && categoryIds.Contains(o.CategoryId))
            .Select(o => o.CategoryId)
            .ToListAsync();

        var toCreate = categoryIds.Except(existingCategoryIds).ToList();
        var now = DateTime.UtcNow;
        // Shared across every row from this one bulk-add so the admin list
        // can group them back into a single card — see
        // VendorCategorySortOrder.GroupId.
        var groupId = Guid.NewGuid().ToString();
        var entities = toCreate.Select(cid => new VendorCategorySortOrder
        {
            VendorId = vendorId,
            CategoryId = cid,
            SortOrder = request.SortOrder,
            GroupId = groupId,
            IsActive = request.Active,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.VendorCategorySortOrders.AddRange(entities);
        await _db.SaveChangesAsync();

        var skipped = existingCategoryIds.Count == 0
            ? new List<object>()
            : (await _db.Categories
                .Where(c => existingCategoryIds.Contains(c.Id))
                .Select(c => new { categoryId = c.Id, categoryName = c.Translations.FirstOrDefault()!.Name })
                .ToListAsync())
                .Select(x => (object)x)
                .ToList();

        var message = existingCategoryIds.Count == 0
            ? $"Added to {entities.Count} categor{(entities.Count == 1 ? "y" : "ies")}."
            : $"Added to {entities.Count} categor{(entities.Count == 1 ? "y" : "ies")}; skipped {existingCategoryIds.Count} that already had a priority for this vendor.";

        return Ok(new
        {
            success = true,
            message,
            data = await ToPriorityDtosAsync(entities),
            skipped
        });
    }

    public record AddCategoriesToPriorityGroupRequest(List<int> CategoryIds);

    /// <summary>Add more categories to an existing bulk-created priority group</summary>
    /// <param name="vendorId">Vendor ID</param>
    /// <param name="groupId">The `groupId` shared by the override's existing rows</param>
    /// <param name="request">Category ids to add to the group</param>
    [HttpPost("{vendorId:int}/category-priority/groups/{groupId}/categories")]
    public async Task<IActionResult> AddCategoriesToPriorityGroup(int vendorId, string groupId, [FromBody] AddCategoriesToPriorityGroupRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");

        var template = await _db.VendorCategorySortOrders
            .Where(o => o.VendorId == vendorId && o.GroupId == groupId)
            .OrderBy(o => o.Id)
            .FirstOrDefaultAsync();
        if (template == null) return NotFound(new { success = false, message = "Priority group not found." });

        var categoryIds = (request.CategoryIds ?? new List<int>()).Distinct().ToList();
        if (categoryIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one category." });

        var foundIds = await _db.Categories.Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missing = categoryIds.Except(foundIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { success = false, message = $"Category id(s) not found: {string.Join(", ", missing)}." });

        var existingCategoryIds = await _db.VendorCategorySortOrders
            .Where(o => o.VendorId == vendorId && categoryIds.Contains(o.CategoryId))
            .Select(o => o.CategoryId)
            .ToListAsync();
        var toCreate = categoryIds.Except(existingCategoryIds).ToList();

        var now = DateTime.UtcNow;
        var entities = toCreate.Select(cid => new VendorCategorySortOrder
        {
            VendorId = vendorId,
            CategoryId = cid,
            SortOrder = template.SortOrder,
            GroupId = groupId,
            IsActive = template.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
        _db.VendorCategorySortOrders.AddRange(entities);
        await _db.SaveChangesAsync();

        var groupSkipped = existingCategoryIds.Count == 0
            ? new List<object>()
            : (await _db.Categories
                .Where(c => existingCategoryIds.Contains(c.Id))
                .Select(c => new { categoryId = c.Id, categoryName = c.Translations.FirstOrDefault()!.Name })
                .ToListAsync())
                .Select(x => (object)x)
                .ToList();

        var groupMessage = existingCategoryIds.Count == 0
            ? $"Added {entities.Count} more categor{(entities.Count == 1 ? "y" : "ies")}."
            : $"Added {entities.Count} more categor{(entities.Count == 1 ? "y" : "ies")}; skipped {existingCategoryIds.Count} that already had a priority.";

        return Ok(new { success = true, message = groupMessage, data = await ToPriorityDtosAsync(entities), skipped = groupSkipped });
    }

    /// <summary>Update a category priority override.</summary>
    [HttpPut("{vendorId:int}/category-priority/{id:int}")]
    public async Task<IActionResult> UpdateCategoryPriority(int vendorId, int id, [FromBody] CategoryPriorityRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");

        var entity = await _db.VendorCategorySortOrders.FirstOrDefaultAsync(o => o.Id == id && o.VendorId == vendorId);
        if (entity == null) return NotFound(new { success = false, message = "Category priority override not found." });
        var priorityError = ValidatePriority(request.SortOrder);
        if (priorityError != null) return BadRequest(new { success = false, message = priorityError });
        if (!await _db.Categories.AnyAsync(c => c.Id == request.CategoryId))
            return BadRequest(new { success = false, message = "Selected category was not found." });
        if (await _db.VendorCategorySortOrders.AnyAsync(o => o.VendorId == vendorId && o.CategoryId == request.CategoryId && o.Id != id))
            return BadRequest(new { success = false, message = "This vendor already has a priority set for this category." });

        entity.CategoryId = request.CategoryId;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Category priority updated.", data = (await ToPriorityDtosAsync(new List<VendorCategorySortOrder> { entity }))[0] });
    }

    /// <summary>Toggle a category priority override active/inactive.</summary>
    [HttpPatch("{vendorId:int}/category-priority/{id:int}/status")]
    public async Task<IActionResult> ToggleCategoryPriorityActive(int vendorId, int id, [FromBody] ToggleActiveRequest request)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");

        var entity = await _db.VendorCategorySortOrders.FirstOrDefaultAsync(o => o.Id == id && o.VendorId == vendorId);
        if (entity == null) return NotFound(new { success = false, message = "Category priority override not found." });

        entity.IsActive = request.Active;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Status updated.", data = (await ToPriorityDtosAsync(new List<VendorCategorySortOrder> { entity }))[0] });
    }

    /// <summary>Delete a category priority override.</summary>
    [HttpDelete("{vendorId:int}/category-priority/{id:int}")]
    public async Task<IActionResult> DeleteCategoryPriority(int vendorId, int id)
    {
        if (!await HasPermissionAsync("vendors", requireWrite: true)) return AdminForbidden("vendors");

        var entity = await _db.VendorCategorySortOrders.FirstOrDefaultAsync(o => o.Id == id && o.VendorId == vendorId);
        if (entity == null) return NotFound(new { success = false, message = "Category priority override not found." });

        _db.VendorCategorySortOrders.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Category priority override removed." });
    }

    public record ToggleActiveRequest(bool Active);

    /// <summary>Resolves each override's category name (batched) in one pass
    /// rather than N+1 lookups.</summary>
    private async Task<List<object>> ToPriorityDtosAsync(List<VendorCategorySortOrder> overrides)
    {
        var categoryIds = overrides.Select(o => o.CategoryId).Distinct().ToList();
        var names = categoryIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Categories
                .Where(c => categoryIds.Contains(c.Id))
                .Select(c => new { c.Id, Name = c.Translations.FirstOrDefault()!.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "");

        return overrides.Select(o => (object)new
        {
            o.Id,
            o.VendorId,
            o.CategoryId,
            CategoryName = names.TryGetValue(o.CategoryId, out var n) ? n : null,
            o.SortOrder,
            o.GroupId,
            o.IsActive,
            o.CreatedAt,
            o.UpdatedAt
        }).ToList();
    }
}
