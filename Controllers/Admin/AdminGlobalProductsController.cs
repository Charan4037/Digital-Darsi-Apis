using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin global products controller for listing, creating, updating, and managing products across all vendors.
/// Routes: /api/v1/admin/global-products
/// This handles the admin view of products with global management capabilities.
/// </summary>
[Route("api/v1/admin/global-products")]
[Tags("Admin – Global Products")]
public class AdminGlobalProductsController : AdminBaseController
{
    private readonly DOSDbContext _db;

    public AdminGlobalProductsController(DOSDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    /// <summary>List all products with search and filter</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? filter = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Where(p => p.ParentId == null)
            .AsNoTracking();

        // Search filter (name, vendor name, SKU)
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(p =>
                p.Flats.Any(f => f.Name != null && f.Name.ToLower().Contains(searchLower)) ||
                (p.Sku != null && p.Sku.ToLower().Contains(searchLower)));
        }

        // Filter
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
            VendorName = "N/A", // TODO: Get vendor name from relationship
            InStock = p.Inventories.Any(i => i.Qty > 0),
            StockQty = p.Inventories.Sum(i => i.Qty),
            AvgRating = 0, // TODO: Calculate from reviews
            ReviewsCount = 0, // TODO: Count reviews
            Active = p.Flats.FirstOrDefault()!.Status ?? false
        }).ToList();

        return Ok(new ProductListResponse
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

    /// <summary>Create a new product</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "name is required" });
        if (string.IsNullOrWhiteSpace(request.Sku))
            return BadRequest(new { message = "sku is required" });
        if (request.Price < 0)
            return BadRequest(new { message = "price must be >= 0" });

        // Check SKU uniqueness
        if (await _db.Products.AnyAsync(p => p.Sku == request.Sku))
            return Conflict(new { message = $"A product with SKU '{request.Sku}' already exists" });

        var now = DateTime.UtcNow;
        
        // Get or create category
        var category = await _db.Categories
            .Include(c => c.Translations)
            .FirstOrDefaultAsync(c => c.Translations.Any(t => t.Name == request.CategoryName));

        if (category == null)
            return BadRequest(new { message = $"Category '{request.CategoryName}' not found" });

        // Create product
        var product = new Models.Catalog.Product
        {
            Sku = request.Sku,
            Type = "simple",
            CreatedAt = now,
            UpdatedAt = now
        };
        
        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        // Create product flat
        var urlKey = Slugify(request.Name);
        if (await _db.ProductFlats.AnyAsync(f => f.UrlKey == urlKey))
            urlKey = $"{urlKey}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var flat = new Models.Catalog.ProductFlat
        {
            ProductId = product.Id,
            Sku = request.Sku,
            Type = "simple",
            Name = request.Name,
            UrlKey = urlKey,
            Price = request.Price,
            SpecialPrice = request.SpecialPrice,
            Status = request.Active,
            Locale = "en",
            Channel = "default",
            VisibleIndividually = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.ProductFlats.Add(flat);

        // Create inventory
        var inventorySources = await _db.InventorySources.FirstOrDefaultAsync();
        var inventorySourceId = inventorySources?.Id ?? 1;

        _db.ProductInventories.Add(new Models.Catalog.ProductInventory
        {
            ProductId = product.Id,
            Qty = request.StockQty,
            InventorySourceId = inventorySourceId
        });

        // Add category
        product.Categories.Add(category);

        // Create price index
        var effectivePrice = request.SpecialPrice.HasValue && request.SpecialPrice > 0 
            ? request.SpecialPrice.Value 
            : request.Price;

        _db.ProductPriceIndices.Add(new Models.Catalog.ProductPriceIndex
        {
            ProductId = product.Id,
            MinPrice = effectivePrice,
            RegularMinPrice = request.Price,
            MaxPrice = effectivePrice,
            RegularMaxPrice = request.Price,
            CreatedAt = now,
            UpdatedAt = now
        });

        await _db.SaveChangesAsync();

        return Ok(new CreatedResponse<AdminProductDto>
        {
            Data = new AdminProductDto
            {
                Id = product.Id,
                Sku = request.Sku,
                Name = request.Name,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                CategoryName = request.CategoryName,
                VendorName = request.VendorName,
                InStock = request.InStock,
                StockQty = request.StockQty,
            },
            Message = $"\"{request.Name}\" added"
        });
    }

    /// <summary>Update an existing product</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProductRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var product = await _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null)
            return NotFound(new { message = "Product not found" });

        // Check SKU uniqueness if changing
        if (!string.IsNullOrWhiteSpace(request.Sku) && request.Sku != product.Sku)
        {
            if (await _db.Products.AnyAsync(p => p.Sku == request.Sku && p.Id != id))
                return Conflict(new { message = $"A product with SKU '{request.Sku}' already exists" });
            product.Sku = request.Sku;
        }

        var flat = product.Flats.FirstOrDefault(f => f.Locale == "en") ?? product.Flats.FirstOrDefault();
        if (flat != null)
        {
            flat.Name = request.Name;
            flat.Price = request.Price;
            flat.SpecialPrice = request.SpecialPrice;
            flat.Status = request.Active;
            flat.UpdatedAt = DateTime.UtcNow;
        }

        // Update inventory
        var inventory = product.Inventories.FirstOrDefault();
        if (inventory != null)
            inventory.Qty = request.StockQty;

        // Update category if needed
        var newCategory = await _db.Categories
            .Include(c => c.Translations)
            .FirstOrDefaultAsync(c => c.Translations.Any(t => t.Name == request.CategoryName));

        if (newCategory != null)
        {
            product.Categories.Clear();
            product.Categories.Add(newCategory);
        }

        await _db.SaveChangesAsync();

        return Ok(new UpdatedResponse<AdminProductDto>
        {
            Data = new AdminProductDto
            {
                Id = product.Id,
                Sku = product.Sku ?? "",
                Name = request.Name,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                CategoryName = request.CategoryName,
                VendorName = request.VendorName,
                InStock = request.InStock,
                StockQty = request.StockQty,
                AvgRating = 0,
                ReviewsCount = 0,
                Active = request.Active
            },
            Message = $"\"{request.Name}\" updated"
        });
    }

    /// <summary>Update product status (activate/deactivate)</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

        var product = await _db.Products
            .Include(p => p.Flats)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null)
            return NotFound(new { message = "Product not found" });

        var flat = product.Flats.FirstOrDefault();
        if (flat != null)
        {
            flat.Status = request.Active.Value;
            await _db.SaveChangesAsync();
        }

        var action = request.Active.Value ? "activated" : "deactivated";
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = product.Id, active = request.Active.Value },
            Message = $"\"{flat?.Name ?? "Product"}\" {action}"
        });
    }

    /// <summary>Delete a product</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var product = await _db.Products.FindAsync(id);
        if (product == null)
            return NotFound(new { message = "Product not found" });

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Product removed" });
    }
}
