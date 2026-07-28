using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Models.Admin;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Admin global products controller for listing, creating, updating, and managing products across all vendors.
/// Routes: /api/v1/admin/global-products
/// This handles the admin view of products with global management capabilities.
/// </summary>
[Route("api/v1/admin/global-products")]
[Tags("Admin � Global Products")]
public class AdminGlobalProductsController : AdminBaseController
{
    private readonly DOSDbContext _db;
    private readonly FirebaseStorageService _storage;
    private readonly ProductService _productService;
    private readonly VendorAggregationService _aggregation;

    public AdminGlobalProductsController(DOSDbContext db, IConfiguration config, FirebaseStorageService storage, ProductService productService, VendorAggregationService aggregation) : base(db, config)
    {
        _db = db;
        _storage = storage;
        _productService = productService;
        _aggregation = aggregation;
    }

    /// <summary>List all products with search and filter</summary>
    /// <param name="categoryId">Restrict to this category AND all of its subcategories</param>
    /// <param name="vendorName">Restrict to products sold by this vendor (exact name match)</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? filter = null,
        [FromQuery] int? categoryId = null,
        [FromQuery] string? vendorName = null)
    {
        if (!await HasPermissionAsync("global_products")) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Include(p => p.Images)
            .Include(p => p.Children)
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

        // Category filter — includes the whole sub-tree, not just direct hits,
        // so filtering by a parent category (e.g. "Groceries") also surfaces
        // products filed only under its children (e.g. "Rice & Grains").
        if (categoryId.HasValue)
        {
            var subtreeIds = await _productService.GetSubtreeCategoryIdsAsync(categoryId.Value);
            query = query.Where(p => p.Categories.Any(c => subtreeIds.Contains(c.Id)));
        }

        // Vendor filter — products carry no vendor FK, so matching is by the
        // same free-text name (vendor_en/vendor_te in Additional) every other
        // vendor-aware admin endpoint uses (see VendorAggregationService).
        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            var productVendorMap = await _aggregation.BuildProductVendorMapAsync();
            var vendorProductIds = productVendorMap
                .Where(kv => string.Equals(kv.Value, vendorName, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToHashSet();
            query = query.Where(p => vendorProductIds.Contains(p.Id));
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
                Name = p.Flats.FirstOrDefault()!.Name ?? "",
                Price = decimal.Parse(p.Flats.FirstOrDefault()!.Price?.ToString() ?? "0"),
                SpecialPrice = p.Flats.FirstOrDefault()!.SpecialPrice,
                CategoryName = p.Categories.FirstOrDefault() != null ?
                    p.Categories.FirstOrDefault()!.Translations.FirstOrDefault()?.Name ?? "" : "",
                CategoryId = p.Categories.FirstOrDefault()?.Id,
                VendorName = ProductService.ExtractVendorName(p.Additional, "en") ?? "",
                InStock = p.Inventories.Any(i => i.Qty > 0),
                StockQty = p.Inventories.Sum(i => i.Qty),
                AvgRating = 0, // TODO: Calculate from reviews
                ReviewsCount = 0, // TODO: Count reviews
                Active = p.Flats.Any(f => f.Status == true),
                ImageId = image?.Id,
                ImageUrl = _productService.GetBaseImageUrl(p),
                VariantCount = p.Children.Count,
                ShortDescription = p.Flats.FirstOrDefault()?.ShortDescription,
                Description = p.Flats.FirstOrDefault()?.Description,
                NameTe = p.Flats.FirstOrDefault(f => f.Locale == "te")?.Name,
                ShortDescriptionTe = p.Flats.FirstOrDefault(f => f.Locale == "te")?.ShortDescription,
                DescriptionTe = p.Flats.FirstOrDefault(f => f.Locale == "te")?.Description
            };
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
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");

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

        // Resolve category by id when given — category names collide across
        // the tree (e.g. "Household Appliances" exists under 5 different
        // parents), so name-only matching can silently tag the wrong branch.
        var (category, categoryError) = await ResolveCategoryAsync(request.CategoryId, request.CategoryName);
        if (categoryError != null)
            return BadRequest(new { message = categoryError });
        if (category == null)
            return BadRequest(new { message = "categoryId or categoryName is required" });

        // Create product. `Additional` carries the free-text vendor name the
        // same way scraped/imported products do (vendor_en/vendor_te — see
        // ProductService.ExtractVendorName) so it round-trips on GET/list
        // instead of vanishing the moment this product is fetched back. Use
        // the vendor's own Telugu name if it already has one on file, rather
        // than blindly mirroring the English name over it.
        var existingVendor = await _db.Vendors.FirstOrDefaultAsync(v => v.Name == request.VendorName);
        var product = new Models.Catalog.Product
        {
            Sku = request.Sku,
            Type = "simple",
            Additional = JsonSerializer.Serialize(new { vendor_en = request.VendorName, vendor_te = existingVendor?.NameTe ?? request.VendorName }),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        // New vendor names typed on this form should show up as real
        // vendors too — not just names baked into scraped catalog data.
        if (!string.IsNullOrWhiteSpace(request.VendorName) && existingVendor == null)
        {
            _db.Vendors.Add(new Models.Catalog.Vendor
            {
                Name = request.VendorName.Trim(),
                NameTe = request.VendorName.Trim(),
                Active = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _db.SaveChangesAsync();
        }

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
            ShortDescription = request.ShortDescription,
            Description = request.Description,
            Locale = "en",
            Channel = "default",
            VisibleIndividually = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.ProductFlats.Add(flat);

        // Telugu flat — same url_key as the English row (confirmed safe: real
        // scraped products already share one url_key across both locale rows).
        // Falls back to the English text for any field the admin left blank on
        // the Telugu tab, so the storefront never has to choose between a
        // missing translation and an empty field.
        var nameTe = !string.IsNullOrWhiteSpace(request.NameTe) ? request.NameTe!.Trim() : request.Name;
        var shortDescTe = !string.IsNullOrWhiteSpace(request.ShortDescriptionTe) ? request.ShortDescriptionTe : request.ShortDescription;
        var descTe = !string.IsNullOrWhiteSpace(request.DescriptionTe) ? request.DescriptionTe : request.Description;
        _db.ProductFlats.Add(new Models.Catalog.ProductFlat
        {
            ProductId = product.Id,
            Sku = request.Sku,
            Type = "simple",
            Name = nameTe,
            UrlKey = urlKey,
            Price = request.Price,
            SpecialPrice = request.SpecialPrice,
            Status = request.Active,
            ShortDescription = shortDescTe,
            Description = descTe,
            Locale = "te",
            Channel = "default",
            VisibleIndividually = true,
            CreatedAt = now,
            UpdatedAt = now
        });

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
            ChannelId = 1, // product_price_indices.channel_id is NOT NULL DEFAULT 1 — EF sends an explicit NULL for an unset nullable column instead of letting the DB default apply
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
                CategoryName = category.Translations.FirstOrDefault()?.Name ?? "",
                CategoryId = category.Id,
                VendorName = request.VendorName,
                InStock = request.StockQty > 0,
                StockQty = request.StockQty,
                ShortDescription = request.ShortDescription,
                Description = request.Description,
                NameTe = nameTe,
                ShortDescriptionTe = shortDescTe,
                DescriptionTe = descTe,
            },
            Message = $"\"{request.Name}\" added"
        });
    }

    /// <summary>Update an existing product</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProductRequest request)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");

        var product = await _db.Products
            .Include(p => p.Flats)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Include(p => p.Images)
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
            if (request.ShortDescription != null) flat.ShortDescription = request.ShortDescription;
            if (request.Description != null) flat.Description = request.Description;
            flat.UpdatedAt = DateTime.UtcNow;
        }

        // Active/inactive is a product-level concept, not a per-locale one —
        // a product with both `en` and `te` flat rows (common on imported
        // catalog data) must have ALL of them updated, or "Any flat active"
        // checks elsewhere (storefront browse/search) keep treating it as
        // active because the untouched locale's row is still status=1.
        foreach (var f in product.Flats)
        {
            f.Status = request.Active;
            f.UpdatedAt = DateTime.UtcNow;
        }

        // Telugu flat — find or create so every edit keeps it in sync instead
        // of leaving it to silently go stale (previously, price/name/status
        // only ever touched the English row, so an edited scraped product's
        // Telugu translation would diverge from the new English content the
        // moment an admin saved a change). Text fields fall back to the
        // English value when the admin leaves the Telugu tab blank.
        var teFlat = product.Flats.FirstOrDefault(f => f.Locale == "te");
        var nameTe = !string.IsNullOrWhiteSpace(request.NameTe) ? request.NameTe!.Trim() : request.Name;
        var shortDescTe = !string.IsNullOrWhiteSpace(request.ShortDescriptionTe)
            ? request.ShortDescriptionTe
            : (request.ShortDescription ?? teFlat?.ShortDescription);
        var descTe = !string.IsNullOrWhiteSpace(request.DescriptionTe)
            ? request.DescriptionTe
            : (request.Description ?? teFlat?.Description);
        if (teFlat != null)
        {
            teFlat.Name = nameTe;
            teFlat.Price = request.Price;
            teFlat.SpecialPrice = request.SpecialPrice;
            teFlat.ShortDescription = shortDescTe;
            teFlat.Description = descTe;
            teFlat.UpdatedAt = DateTime.UtcNow;
        }
        else if (flat != null)
        {
            _db.ProductFlats.Add(new Models.Catalog.ProductFlat
            {
                ProductId = product.Id,
                Sku = product.Sku ?? flat.Sku,
                Type = "simple",
                Name = nameTe,
                UrlKey = flat.UrlKey,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                Status = request.Active,
                ShortDescription = shortDescTe,
                Description = descTe,
                Locale = "te",
                Channel = "default",
                VisibleIndividually = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Update inventory — most scraped/imported products never got a
        // ProductInventory row in the first place (no stock was ever tracked
        // for them), so silently no-op'ing when one doesn't exist meant a
        // stock-quantity edit here would "succeed" but never actually save.
        // Create the row on first edit instead.
        var inventory = product.Inventories.FirstOrDefault();
        if (inventory != null)
        {
            inventory.Qty = request.StockQty;
        }
        else
        {
            var inventorySource = await _db.InventorySources.FirstOrDefaultAsync();
            _db.ProductInventories.Add(new Models.Catalog.ProductInventory
            {
                ProductId = product.Id,
                Qty = request.StockQty,
                InventorySourceId = inventorySource?.Id ?? 1
            });
        }

        // Update category if a change was actually requested.
        Models.Catalog.Category? newCategory = null;
        if (request.CategoryId.HasValue || !string.IsNullOrWhiteSpace(request.CategoryName))
        {
            var (resolved, categoryError) = await ResolveCategoryAsync(request.CategoryId, request.CategoryName);
            if (categoryError != null)
                return BadRequest(new { message = categoryError });

            newCategory = resolved;
            product.Categories.Clear();
            product.Categories.Add(resolved!);
        }

        // Keep the free-text vendor name (Additional JSON) in sync — same
        // mechanism the scraped catalog uses, see ProductService.ExtractVendorName.
        // Uses the vendor's own Telugu name if it already has one on file,
        // rather than blindly mirroring the English name over it.
        if (!string.IsNullOrWhiteSpace(request.VendorName))
        {
            var existingVendor = await _db.Vendors.FirstOrDefaultAsync(v => v.Name == request.VendorName);
            product.Additional = JsonSerializer.Serialize(new { vendor_en = request.VendorName, vendor_te = existingVendor?.NameTe ?? request.VendorName });
            if (existingVendor == null)
            {
                _db.Vendors.Add(new Models.Catalog.Vendor
                {
                    Name = request.VendorName.Trim(),
                    NameTe = request.VendorName.Trim(),
                    Active = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();

        var image = product.Images.OrderBy(i => i.Position).FirstOrDefault();
        var displayedCategory = newCategory ?? product.Categories.FirstOrDefault();
        return Ok(new UpdatedResponse<AdminProductDto>
        {
            Data = new AdminProductDto
            {
                Id = product.Id,
                Sku = product.Sku ?? "",
                Name = request.Name,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                CategoryName = displayedCategory?.Translations.FirstOrDefault()?.Name ?? "",
                CategoryId = displayedCategory?.Id,
                VendorName = request.VendorName,
                InStock = request.StockQty > 0,
                StockQty = request.StockQty,
                AvgRating = 0,
                ReviewsCount = 0,
                Active = request.Active,
                ImageId = image?.Id,
                ImageUrl = _productService.GetBaseImageUrl(product),
                ShortDescription = product.Flats.FirstOrDefault(f => f.Locale == "en")?.ShortDescription,
                Description = product.Flats.FirstOrDefault(f => f.Locale == "en")?.Description,
                NameTe = nameTe,
                ShortDescriptionTe = shortDescTe,
                DescriptionTe = descTe,
            },
            Message = $"\"{request.Name}\" updated"
        });
    }

    // ─── Variants ─────────────────────────────────────────────────────────
    // A variant is a real, independently-purchasable Product row (ParentId
    // pointing at this product) with its own SKU/price/stock — see
    // ProductService.GetProductVariations for how the storefront reads
    // these back, and Data/DigitalDarsiSeeder.cs CreateChildVariantAsync for
    // the original scraped-data creation pattern this mirrors. Existing
    // scraped variants have no inventory row (always shown in-stock by
    // ProductService.IsSaleable's fallback); variants created/edited here
    // always get a real inventory row so stock control is meaningful.

    private AdminProductVariantDto ToVariantDto(Models.Catalog.Product child)
    {
        var extras = ProductService.ReadChildVariantExtras(child);
        var flat = child.Flats.FirstOrDefault(f => f.Locale == "en") ?? child.Flats.FirstOrDefault();
        var stockQty = child.Inventories.Sum(i => i.Qty);
        return new AdminProductVariantDto
        {
            Id = child.Id,
            Value = !string.IsNullOrEmpty(extras.Value) ? extras.Value : (flat?.Name ?? child.Sku),
            Label = !string.IsNullOrEmpty(extras.Label) ? extras.Label : "Variant",
            Price = flat?.Price ?? 0,
            SpecialPrice = flat?.SpecialPrice,
            StockQty = stockQty,
            InStock = child.Inventories.Any() ? stockQty > 0 : true,
            Active = flat?.Status ?? false
        };
    }

    /// <summary>List a product's variants</summary>
    [HttpGet("{id:int}/variants")]
    public async Task<IActionResult> GetVariants(int id)
    {
        if (!await HasPermissionAsync("global_products")) return AdminUnauthorized();

        var exists = await _db.Products.AnyAsync(p => p.Id == id && p.ParentId == null);
        if (!exists) return NotFound(new { message = "Product not found" });

        var children = await _db.Products
            .Include(c => c.Flats)
            .Include(c => c.Inventories)
            .Where(c => c.ParentId == id)
            .AsNoTracking()
            .ToListAsync();

        return Ok(new VariantListResponse { Data = children.Select(ToVariantDto).ToList() });
    }

    /// <summary>Add a new variant (e.g. "1kg") to a product</summary>
    [HttpPost("{id:int}/variants")]
    public async Task<IActionResult> CreateVariant(int id, [FromBody] CreateVariantRequest request)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");
        if (string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { message = "value is required" });
        if (request.Price < 0)
            return BadRequest(new { message = "price must be >= 0" });

        var parent = await _db.Products
            .Include(p => p.Flats)
            .FirstOrDefaultAsync(p => p.Id == id && p.ParentId == null);
        if (parent == null)
            return NotFound(new { message = "Product not found" });

        var value = request.Value.Trim();
        var label = string.IsNullOrWhiteSpace(request.Label) ? "Variant" : request.Label!.Trim();
        var parentFlatEn = parent.Flats.FirstOrDefault(f => f.Locale == "en") ?? parent.Flats.FirstOrDefault();
        var parentFlatTe = parent.Flats.FirstOrDefault(f => f.Locale == "te");

        var slug = Slugify(value);
        if (string.IsNullOrEmpty(slug)) slug = "variant";
        var childSku = Truncate($"{parent.Sku}-{slug}", 60);
        if (await _db.Products.AnyAsync(p => p.Sku == childSku))
            childSku = Truncate($"{childSku}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", 60);

        var childUrlKeyBase = $"{parentFlatEn?.UrlKey}-{slug}";
        var childUrlKey = childUrlKeyBase;
        if (await _db.ProductFlats.AnyAsync(f => f.UrlKey == childUrlKey))
            childUrlKey = $"{childUrlKeyBase}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var now = DateTime.UtcNow;
        var child = new Models.Catalog.Product
        {
            Sku = childSku,
            ParentId = parent.Id,
            Type = "simple",
            AttributeFamilyId = parent.AttributeFamilyId,
            Additional = JsonSerializer.Serialize(new
            {
                variant_value = value,
                variant_label = label,
                variant_position = 0
            }),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Products.Add(child);
        await _db.SaveChangesAsync();

        _db.ProductFlats.Add(new Models.Catalog.ProductFlat
        {
            ProductId = child.Id,
            ParentId = parentFlatEn?.Id,
            Sku = childSku,
            Type = "simple",
            Name = $"{parentFlatEn?.Name} - {value}",
            UrlKey = childUrlKey,
            Status = request.Active,
            VisibleIndividually = false,
            Price = request.Price,
            SpecialPrice = request.SpecialPrice,
            Weight = 1,
            Locale = "en",
            Channel = "default",
            AttributeFamilyId = parent.AttributeFamilyId,
            CreatedAt = now,
            UpdatedAt = now
        });
        if (parentFlatTe != null)
        {
            _db.ProductFlats.Add(new Models.Catalog.ProductFlat
            {
                ProductId = child.Id,
                ParentId = parentFlatTe.Id,
                Sku = childSku,
                Type = "simple",
                Name = $"{parentFlatTe.Name} - {value}",
                UrlKey = childUrlKey,
                Status = request.Active,
                VisibleIndividually = false,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                Weight = 1,
                Locale = "te",
                Channel = "default",
                AttributeFamilyId = parent.AttributeFamilyId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var inventorySource = await _db.InventorySources.FirstOrDefaultAsync();
        _db.ProductInventories.Add(new Models.Catalog.ProductInventory
        {
            ProductId = child.Id,
            Qty = request.StockQty,
            InventorySourceId = inventorySource?.Id ?? 1
        });

        await _db.SaveChangesAsync();

        return Ok(new CreatedResponse<AdminProductVariantDto>
        {
            Data = new AdminProductVariantDto
            {
                Id = child.Id,
                Value = value,
                Label = label,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                StockQty = request.StockQty,
                InStock = request.StockQty > 0,
                Active = request.Active
            },
            Message = $"Variant \"{value}\" added"
        });
    }

    /// <summary>Update a variant's value/price/stock/status</summary>
    [HttpPut("{id:int}/variants/{variantId:int}")]
    public async Task<IActionResult> UpdateVariant(int id, int variantId, [FromBody] UpdateVariantRequest request)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");
        if (string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { message = "value is required" });
        if (request.Price < 0)
            return BadRequest(new { message = "price must be >= 0" });

        var child = await _db.Products
            .Include(c => c.Flats)
            .Include(c => c.Inventories)
            .FirstOrDefaultAsync(c => c.Id == variantId && c.ParentId == id);
        if (child == null)
            return NotFound(new { message = "Variant not found" });

        var value = request.Value.Trim();
        var label = string.IsNullOrWhiteSpace(request.Label) ? "Variant" : request.Label!.Trim();

        child.Additional = JsonSerializer.Serialize(new
        {
            variant_value = value,
            variant_label = label,
            variant_position = 0
        });

        foreach (var f in child.Flats)
        {
            var baseName = f.Name?.Split(" - ").FirstOrDefault() ?? f.Name ?? "";
            f.Name = $"{baseName} - {value}";
            f.Price = request.Price;
            f.SpecialPrice = request.SpecialPrice;
            f.Status = request.Active;
            f.UpdatedAt = DateTime.UtcNow;
        }

        var inventory = child.Inventories.FirstOrDefault();
        if (inventory != null)
        {
            inventory.Qty = request.StockQty;
        }
        else
        {
            var inventorySource = await _db.InventorySources.FirstOrDefaultAsync();
            _db.ProductInventories.Add(new Models.Catalog.ProductInventory
            {
                ProductId = child.Id,
                Qty = request.StockQty,
                InventorySourceId = inventorySource?.Id ?? 1
            });
        }

        await _db.SaveChangesAsync();

        return Ok(new UpdatedResponse<AdminProductVariantDto>
        {
            Data = new AdminProductVariantDto
            {
                Id = child.Id,
                Value = value,
                Label = label,
                Price = request.Price,
                SpecialPrice = request.SpecialPrice,
                StockQty = request.StockQty,
                InStock = request.StockQty > 0,
                Active = request.Active
            },
            Message = $"Variant \"{value}\" updated"
        });
    }

    /// <summary>Remove a variant entirely</summary>
    [HttpDelete("{id:int}/variants/{variantId:int}")]
    public async Task<IActionResult> DeleteVariant(int id, int variantId)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");

        var child = await _db.Products
            .Include(c => c.Flats)
            .Include(c => c.Inventories)
            .Include(c => c.Images)
            .FirstOrDefaultAsync(c => c.Id == variantId && c.ParentId == id);
        if (child == null)
            return NotFound(new { message = "Variant not found" });

        _db.ProductFlats.RemoveRange(child.Flats);
        _db.ProductInventories.RemoveRange(child.Inventories);
        _db.ProductImages.RemoveRange(child.Images);
        _db.Products.Remove(child);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Variant removed" });
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>
    /// Resolves a category by id when given (preferred — unambiguous), falling
    /// back to a name match otherwise. Category names collide across the tree
    /// (e.g. "Household Appliances" exists under 5 different parents), so
    /// id-based lookup is the only way to unambiguously tag a specific branch.
    /// </summary>
    private async Task<(Models.Catalog.Category? Category, string? Error)> ResolveCategoryAsync(int? categoryId, string? categoryName)
    {
        if (categoryId.HasValue)
        {
            var cat = await _db.Categories.Include(c => c.Translations).FirstOrDefaultAsync(c => c.Id == categoryId.Value);
            return cat == null ? (null, $"Category id {categoryId} not found") : (cat, null);
        }
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var cat = await _db.Categories.Include(c => c.Translations)
                .FirstOrDefaultAsync(c => c.Translations.Any(t => t.Name == categoryName));
            return cat == null ? (null, $"Category '{categoryName}' not found") : (cat, null);
        }
        return (null, null);
    }

    /// <summary>List a product's full image gallery, ordered by display position (position 0 = primary/first).</summary>
    [HttpGet("{id:int}/images")]
    public async Task<IActionResult> GetImages(int id)
    {
        if (!await HasPermissionAsync("global_products")) return AdminUnauthorized();

        var product = await _db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id);
        if (product == null)
            return NotFound(new { message = "Product not found" });

        var images = product.Images
            .OrderBy(i => i.Position)
            .Select(i => new AdminProductImageDto { Id = i.Id, Url = _productService.GetImagePublicPath(i), Position = i.Position })
            .ToList();

        return Ok(new { data = images });
    }

    /// <summary>
    /// Add a new image to the product's gallery — appended after existing
    /// images, does not touch/replace any of them. A product can carry
    /// several images (e.g. different angles); the lowest-position one is
    /// used as the thumbnail everywhere else in the app. Max 3MB per image —
    /// a product listing photo, not a full-resolution camera original.
    /// </summary>
    [HttpPost("{id:int}/images")]
    [RequestSizeLimit(3_000_000)]
    public async Task<IActionResult> AddImage(int id, IFormFile? image)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "image file is required" });

        var product = await _db.Products
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (product == null)
            return NotFound(new { message = "Product not found" });

        var safeName = FirebaseStorageService.SanitiseFileName(image.FileName);
        var storagePath = $"products/{product.Sku}/{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{safeName}";
        await using var stream = image.OpenReadStream();
        var (url, error) = await _storage.UploadFromStreamAsync(stream, storagePath, image.ContentType);
        if (error != null)
            return StatusCode(502, new { message = $"Image upload failed: {error}" });

        var nextPosition = product.Images.Count == 0 ? 0 : product.Images.Max(i => i.Position) + 1;
        var newImage = new Models.Catalog.ProductImage
        {
            ProductId = product.Id,
            Path = url!,
            Position = nextPosition
        };
        _db.ProductImages.Add(newImage);
        await _db.SaveChangesAsync();

        return Ok(new UpdatedResponse<AdminProductImageDto>
        {
            Data = new AdminProductImageDto { Id = newImage.Id, Url = url, Position = nextPosition },
            Message = "Image added"
        });
    }

    /// <summary>Remove one image from the product's gallery.</summary>
    [HttpDelete("{id:int}/images/{imageId:int}")]
    public async Task<IActionResult> DeleteImage(int id, int imageId)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");

        var image = await _db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == id);
        if (image == null)
            return NotFound(new { message = "Image not found" });

        // File in Firebase Storage is not deleted, only the DB record —
        // same trade-off the legacy AdminProductController.DeleteImage makes.
        _db.ProductImages.Remove(image);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Image removed" });
    }

    /// <summary>Make an image the primary one (shown as the thumbnail everywhere else) by moving it before all its siblings.</summary>
    [HttpPatch("{id:int}/images/{imageId:int}/set-primary")]
    public async Task<IActionResult> SetPrimaryImage(int id, int imageId)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");

        var images = await _db.ProductImages.Where(i => i.ProductId == id).ToListAsync();
        var target = images.FirstOrDefault(i => i.Id == imageId);
        if (target == null)
            return NotFound(new { message = "Image not found" });

        var minPosition = images.Where(i => i.Id != imageId).Select(i => (int?)i.Position).Min() ?? 0;
        target.Position = minPosition - 1;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Primary image updated" });
    }

    /// <summary>Update product status (activate/deactivate)</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");
        if (!request.Active.HasValue)
            return BadRequest(new { message = "active field is required" });

        var product = await _db.Products
            .Include(p => p.Flats)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null)
            return NotFound(new { message = "Product not found" });

        // Update ALL locale flats — see the comment in Update() above on why
        // a single-flat toggle leaves the product visible via other locales.
        foreach (var f in product.Flats)
        {
            f.Status = request.Active.Value;
        }
        await _db.SaveChangesAsync();

        var action = request.Active.Value ? "activated" : "deactivated";
        var name = product.Flats.FirstOrDefault(f => f.Locale == "en")?.Name ?? product.Flats.FirstOrDefault()?.Name;
        return Ok(new UpdatedResponse<dynamic>
        {
            Data = new { id = product.Id, active = request.Active.Value },
            Message = $"\"{name ?? "Product"}\" {action}"
        });
    }

    /// <summary>Delete a product</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await HasPermissionAsync("global_products", requireWrite: true)) return AdminForbidden("global_products");

        var product = await _db.Products.FindAsync(id);
        if (product == null)
            return NotFound(new { message = "Product not found" });

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Product removed" });
    }
}
