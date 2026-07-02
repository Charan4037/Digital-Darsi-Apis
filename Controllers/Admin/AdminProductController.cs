using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Admin;

/// <summary>
/// Admin CRUD for products.
/// Routes: /api/v1/admin/products
///
/// Product data is written to three tables simultaneously:
///   • products          — base entity (sku, type, family)
///   • product_flat      — denormalised display record (name, price, url_key …)
///   • product_attribute_values — EAV attribute store
///   • product_inventories      — stock quantity per inventory source
///   • product_images            — uploaded product images
///   • product_price_indices     — price cache used by queries
///
/// Images are accepted as IFormFile[] and uploaded to Firebase Storage.
/// </summary>
[Route("api/v1/admin/products")]
[Tags("Admin – Products")]
public class AdminProductController : AdminBaseController
{
    private readonly BagistoDbContext _db;
    private readonly FirebaseStorageService _storage;

    // Core attribute IDs loaded once from the DB (thread-safe: only read
    // after the first request that triggers loading).
    private static AttrMap? _attrs;
    private static readonly SemaphoreSlim _attrLock = new(1, 1);

    private record AttrMap(
        int Name, int Sku, int UrlKey, int Description, int ShortDescription,
        int Price, int SpecialPrice, int Status, int Weight, int Featured, int New);

    public AdminProductController(
        BagistoDbContext db,
        FirebaseStorageService storage,
        IConfiguration config) : base(config)
    {
        _db = db;
        _storage = storage;
    }

    // ─── List ─────────────────────────────────────────────────────────────

    /// <summary>List all products</summary>
    /// <remarks>
    /// Returns products with their price, stock quantity, images and category assignments.
    /// Results are paginated — use `page` and `limit` to navigate.
    ///
    /// **Filter examples:**
    /// - Only active products: `?status=true`
    /// - Only hidden products: `?status=false`
    /// - Products in a specific category: `?categoryId=25`
    /// - Search by name: `?search=chicken`
    ///
    /// **Response includes per product:** id, sku, name, price, special_price, stock_qty, status, images[], categories[]
    /// </remarks>
    /// <param name="page">Page number starting from 1. Default: 1</param>
    /// <param name="limit">Products per page (max 100). Default: 20</param>
    /// <param name="categoryId">Filter to products assigned to this category ID</param>
    /// <param name="status">true = active/visible products only | false = hidden products only</param>
    /// <param name="search">Search by product name (partial match)</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] int? categoryId = null,
        [FromQuery] bool? status = null,
        [FromQuery] string? search = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Products
            .AsSplitQuery()
            .Include(p => p.Flats)
            .Include(p => p.Images)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .AsNoTracking()
            .Where(p => p.ParentId == null); // top-level products only

        if (categoryId.HasValue)
            query = query.Where(p => p.Categories.Any(c => c.Id == categoryId.Value));

        if (status.HasValue)
            query = query.Where(p => p.Flats.Any(f => f.Status == status.Value && f.Locale == "en"));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Flats.Any(f => f.Name != null && f.Name.Contains(search)));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data    = items.Select(p => FormatProduct(p)).ToList(),
            meta    = new { total, page, limit, pages = (int)Math.Ceiling(total / (double)limit) }
        });
    }

    // ─── Get single ───────────────────────────────────────────────────────

    /// <summary>Get full details of a single product</summary>
    /// <remarks>
    /// Returns all product information including description, meta fields, all images, current stock, and category assignments.
    /// Use this when opening a product to edit it in the admin panel.
    /// </remarks>
    /// <param name="id">Product database ID (from the List endpoint)</param>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var p = await _db.Products
            .AsSplitQuery()
            .Include(p => p.Flats)
            .Include(p => p.Images)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .Include(p => p.AttributeFamily)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (p == null) return NotFound(new { success = false, message = "Product not found." });
        return Ok(new { success = true, data = FormatProduct(p, detail: true) });
    }

    // ─── Create ───────────────────────────────────────────────────────────

    /// <summary>Create a new product</summary>
    /// <remarks>
    /// Creates a product and saves it across all required tables (products, product_flat, attribute values, inventory, price index).
    /// Send as **multipart/form-data** so you can attach image files.
    ///
    /// **Required fields:** `name`, `sku`, `price`, `stockQty`
    ///
    /// **Field guide:**
    /// - `name` — Product display name shown in the app (e.g. "Fresh Tomatoes 1kg")
    /// - `sku` — Unique product code (e.g. "FOOD-TOM-001"). Must be unique — API returns 409 if duplicate
    /// - `price` — Regular selling price in ₹ (e.g. 49.00)
    /// - `stockQty` — Number of units available in stock (e.g. 100)
    /// - `urlKey` — URL-friendly slug (e.g. "fresh-tomatoes-1kg"). Auto-generated from name if left blank
    /// - `shortDescription` — Brief one-liner (optional)
    /// - `description` — Full product description shown on the detail page (optional)
    /// - `specialPrice` — Discounted price shown as sale price. Leave blank for no discount
    /// - `weight` — Product weight in kg (optional, used for shipping)
    /// - `status` — true = visible in app, false = hidden. Default: true
    /// - `featured` — true = show in "Featured Products" section. Default: false
    /// - `isNew` — true = show "New" badge on product. Default: false
    /// - `categoryIds` — Comma-separated list of category IDs to assign this product to (e.g. "3,7,12")
    ///   - Get category IDs from `GET /api/v1/admin/categories`
    /// - `attributeFamilyId` — Which attribute family to use. Get IDs from `GET /api/v1/admin/attribute-families`. Uses first available if not set
    /// - `images` — One or more product image files (jpg, png, webp). First image becomes the main thumbnail
    ///
    /// **Example (minimum required fields):**
    ///
    /// Send as form-data:
    /// ```
    /// name        = Fresh Tomatoes 1kg
    /// sku         = FOOD-TOM-001
    /// price       = 49
    /// stockQty    = 100
    /// categoryIds = 25,30
    /// images      = [attach file]
    /// ```
    /// </remarks>
    /// <param name="name">Product display name — required</param>
    /// <param name="sku">Unique product code — required, must not already exist</param>
    /// <param name="price">Regular price in ₹ — required</param>
    /// <param name="stockQty">Stock quantity available — required</param>
    /// <param name="urlKey">URL slug — auto-generated from name if blank</param>
    /// <param name="shortDescription">Brief one-liner</param>
    /// <param name="description">Full description shown on product detail page</param>
    /// <param name="specialPrice">Sale/discount price. Leave blank for no discount</param>
    /// <param name="weight">Weight in kg (used for shipping calculations)</param>
    /// <param name="status">true = visible in app. Default: true</param>
    /// <param name="featured">true = appears in featured section. Default: false</param>
    /// <param name="isNew">true = shows "New" badge. Default: false</param>
    /// <param name="categoryIds">Comma-separated category IDs, e.g. "3,7,12"</param>
    /// <param name="attributeFamilyId">Attribute family ID (from GET /api/v1/admin/attribute-families)</param>
    /// <param name="images">Product image files — first image is the main/thumbnail image</param>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create(
        [FromForm] string name,
        [FromForm] string sku,
        [FromForm] decimal price,
        [FromForm] int stockQty,
        [FromForm] string? urlKey          = null,
        [FromForm] string? shortDescription = null,
        [FromForm] string? description      = null,
        [FromForm] decimal? specialPrice    = null,
        [FromForm] decimal? weight          = null,
        [FromForm] bool status              = true,
        [FromForm] bool featured            = false,
        [FromForm] bool isNew               = false,
        [FromForm] string? categoryIds      = null,
        [FromForm] int? attributeFamilyId   = null,
        IFormFileCollection? images         = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { success = false, message = "name is required." });
        if (string.IsNullOrWhiteSpace(sku))
            return BadRequest(new { success = false, message = "sku is required." });
        if (price < 0)
            return BadRequest(new { success = false, message = "price must be ≥ 0." });

        // Ensure SKU is unique
        if (await _db.Products.AnyAsync(p => p.Sku == sku))
            return Conflict(new { success = false, message = $"SKU '{sku}' already exists." });

        var attrs  = await GetAttrMapAsync();
        var familyId = attributeFamilyId
                       ?? await _db.AttributeFamilies.Select(f => (int?)f.Id).FirstOrDefaultAsync()
                       ?? 1;

        var inventorySourceId = await _db.InventorySources
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync() ?? 1;

        var resolvedUrlKey = string.IsNullOrWhiteSpace(urlKey)
            ? Slugify(name)
            : Slugify(urlKey);

        // Append timestamp if url_key already exists
        if (await _db.ProductFlats.AnyAsync(f => f.UrlKey == resolvedUrlKey))
            resolvedUrlKey = $"{resolvedUrlKey}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var now = DateTime.UtcNow;
        var channelCode = await _db.Channels.Select(c => (string?)c.Code).FirstOrDefaultAsync() ?? "default";

        // 1. products row
        var product = new Product
        {
            Sku               = sku,
            Type              = "simple",
            AttributeFamilyId = familyId,
            CreatedAt         = now,
            UpdatedAt         = now,
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        // 2. product_flat row
        var flat = new ProductFlat
        {
            ProductId           = product.Id,
            Sku                 = sku,
            Type                = "simple",
            Name                = name,
            ShortDescription    = shortDescription,
            Description         = description,
            UrlKey              = resolvedUrlKey,
            Price               = price,
            SpecialPrice        = specialPrice,
            Weight              = weight,
            Status              = status,
            Featured            = featured,
            New                 = isNew,
            Locale              = "en",
            Channel             = channelCode,
            AttributeFamilyId   = familyId,
            VisibleIndividually = true,
            CreatedAt           = now,
            UpdatedAt           = now,
        };
        _db.ProductFlats.Add(flat);

        // 3. product_attribute_values (EAV)
        var attrValues = BuildAttributeValues(product.Id, attrs, sku, name, resolvedUrlKey,
            description, shortDescription, price, specialPrice, weight, status, featured, isNew, "en", channelCode);
        _db.ProductAttributeValues.AddRange(attrValues);

        // 4. product_inventories
        _db.ProductInventories.Add(new ProductInventory
        {
            ProductId         = product.Id,
            Qty               = stockQty,
            VendorId          = 0,
            InventorySourceId = inventorySourceId,
        });

        // 5. product_price_indices
        var effectivePrice = specialPrice.HasValue && specialPrice > 0 ? specialPrice.Value : price;
        _db.ProductPriceIndices.Add(new ProductPriceIndex
        {
            ProductId        = product.Id,
            MinPrice         = effectivePrice,
            RegularMinPrice  = price,
            MaxPrice         = effectivePrice,
            RegularMaxPrice  = price,
            CreatedAt        = now,
            UpdatedAt        = now,
        });

        await _db.SaveChangesAsync();

        // 6. Category assignments
        if (!string.IsNullOrWhiteSpace(categoryIds))
        {
            var ids = ParseIntList(categoryIds);
            var cats = await _db.Categories.Where(c => ids.Contains(c.Id)).ToListAsync();
            foreach (var cat in cats) product.Categories.Add(cat);
            await _db.SaveChangesAsync();
        }

        // 7. Images
        if (images != null && images.Count > 0)
            await UploadAndSaveImagesAsync(product.Id, sku, images);

        // Reload for response
        var saved = await _db.Products
            .AsSplitQuery()
            .Include(p => p.Flats)
            .Include(p => p.Images)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .AsNoTracking()
            .FirstAsync(p => p.Id == product.Id);

        return CreatedAtAction(nameof(Get), new { id = product.Id },
            new { success = true, message = "Product created.", data = FormatProduct(saved, detail: true) });
    }

    // ─── Update ───────────────────────────────────────────────────────────

    /// <summary>Update an existing product</summary>
    /// <remarks>
    /// Updates a product's details. Send as **multipart/form-data**.
    /// **All fields are optional** — only the fields you include are changed. Omitted fields stay as they are.
    ///
    /// **Important notes:**
    /// - `categoryIds` — If provided, **replaces** all current category assignments completely. Send all desired category IDs, not just new ones
    /// - `images` — New images are **appended** to the product's existing images (not replaced). Use `DELETE /{id}/images/{imageId}` to remove specific images
    /// - `specialPrice` — Send `0` to remove an existing special price
    /// - `sku` — Can be changed only if the new SKU doesn't already exist on another product
    ///
    /// **Example — just update price and stock:**
    /// ```
    /// price    = 55
    /// stockQty = 80
    /// ```
    /// </remarks>
    /// <param name="id">Product ID to update</param>
    /// <param name="name">New product name</param>
    /// <param name="sku">New SKU (must be unique)</param>
    /// <param name="price">New regular price in ₹</param>
    /// <param name="stockQty">New stock quantity</param>
    /// <param name="urlKey">New URL slug</param>
    /// <param name="shortDescription">New short description</param>
    /// <param name="description">New full description</param>
    /// <param name="specialPrice">New sale price. Send 0 to remove an existing discount</param>
    /// <param name="weight">New weight in kg</param>
    /// <param name="status">true = visible, false = hidden</param>
    /// <param name="featured">true = show in featured section</param>
    /// <param name="isNew">true = show "New" badge</param>
    /// <param name="categoryIds">Comma-separated category IDs — REPLACES all existing assignments</param>
    /// <param name="images">New image files to ADD to this product (existing images are kept)</param>
    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Update(
        int id,
        [FromForm] string? name              = null,
        [FromForm] string? sku               = null,
        [FromForm] decimal? price            = null,
        [FromForm] int? stockQty             = null,
        [FromForm] string? urlKey            = null,
        [FromForm] string? shortDescription  = null,
        [FromForm] string? description       = null,
        [FromForm] decimal? specialPrice     = null,
        [FromForm] decimal? weight           = null,
        [FromForm] bool? status              = null,
        [FromForm] bool? featured            = null,
        [FromForm] bool? isNew               = null,
        [FromForm] string? categoryIds       = null,
        IFormFileCollection? images          = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var product = await _db.Products
            .AsSplitQuery()
            .Include(p => p.Flats)
            .Include(p => p.Images)
            .Include(p => p.Inventories)
            .Include(p => p.Categories)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null) return NotFound(new { success = false, message = "Product not found." });

        if (!string.IsNullOrWhiteSpace(sku) && sku != product.Sku)
        {
            if (await _db.Products.AnyAsync(p => p.Sku == sku && p.Id != id))
                return Conflict(new { success = false, message = $"SKU '{sku}' already exists." });
            product.Sku = sku;
        }

        var flat = product.Flats.FirstOrDefault(f => f.Locale == "en") ?? product.Flats.FirstOrDefault();
        var now  = DateTime.UtcNow;
        product.UpdatedAt = now;

        if (flat != null)
        {
            if (name != null)              flat.Name             = name;
            if (shortDescription != null)  flat.ShortDescription = shortDescription;
            if (description != null)       flat.Description      = description;
            if (price.HasValue)            flat.Price            = price.Value;
            if (specialPrice.HasValue)     flat.SpecialPrice     = specialPrice.Value == 0 ? null : specialPrice;
            if (weight.HasValue)           flat.Weight           = weight.Value == 0 ? null : weight;
            if (status.HasValue)           flat.Status           = status.Value;
            if (featured.HasValue)         flat.Featured         = featured.Value;
            if (isNew.HasValue)            flat.New              = isNew.Value;
            if (!string.IsNullOrWhiteSpace(urlKey))
            {
                var resolvedKey = Slugify(urlKey);
                if (!await _db.ProductFlats.AnyAsync(f => f.UrlKey == resolvedKey && f.ProductId != id))
                    flat.UrlKey = resolvedKey;
            }
            flat.UpdatedAt = now;
        }

        // Update inventory if supplied
        if (stockQty.HasValue)
        {
            var inv = product.Inventories.FirstOrDefault();
            if (inv != null) inv.Qty = stockQty.Value;
        }

        // Update price index
        if (price.HasValue)
        {
            var idx = await _db.ProductPriceIndices.FirstOrDefaultAsync(x => x.ProductId == id);
            if (idx != null)
            {
                var effectivePrice = specialPrice.HasValue && specialPrice > 0
                    ? specialPrice.Value : price.Value;
                idx.MinPrice        = effectivePrice;
                idx.RegularMinPrice = price.Value;
                idx.MaxPrice        = effectivePrice;
                idx.RegularMaxPrice = price.Value;
                idx.UpdatedAt       = now;
            }
        }

        // Replace category assignments if provided
        if (!string.IsNullOrWhiteSpace(categoryIds))
        {
            product.Categories.Clear();
            var ids  = ParseIntList(categoryIds);
            var cats = await _db.Categories.Where(c => ids.Contains(c.Id)).ToListAsync();
            foreach (var cat in cats) product.Categories.Add(cat);
        }

        await _db.SaveChangesAsync();

        // Append new images
        if (images != null && images.Count > 0)
            await UploadAndSaveImagesAsync(product.Id, product.Sku, images);

        var saved = await _db.Products
            .AsSplitQuery()
            .Include(p => p.Flats)
            .Include(p => p.Images)
            .Include(p => p.Inventories)
            .Include(p => p.Categories).ThenInclude(c => c.Translations)
            .AsNoTracking()
            .FirstAsync(p => p.Id == id);

        return Ok(new { success = true, message = "Product updated.", data = FormatProduct(saved, detail: true) });
    }

    // ─── Add images ───────────────────────────────────────────────────────

    /// <summary>Add more images to an existing product</summary>
    /// <remarks>
    /// Uploads one or more image files and adds them to the product's image gallery.
    /// Existing images are kept — this only adds new ones.
    ///
    /// **Accepted formats:** jpg, jpeg, png, webp, gif
    ///
    /// **Use this when:** You want to add extra photos to a product without going through the full update endpoint.
    ///
    /// To remove a specific image use `DELETE /api/v1/admin/products/{id}/images/{imageId}`
    /// </remarks>
    /// <param name="id">Product ID</param>
    /// <param name="images">One or more image files to upload and attach</param>
    [HttpPost("{id:int}/images")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddImages(int id, IFormFileCollection images)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound(new { success = false, message = "Product not found." });
        if (images == null || images.Count == 0)
            return BadRequest(new { success = false, message = "No images supplied." });

        var urls = await UploadAndSaveImagesAsync(product.Id, product.Sku, images);
        return Ok(new { success = true, message = $"{urls.Count} image(s) uploaded.", urls });
    }

    // ─── Delete image ─────────────────────────────────────────────────────

    /// <summary>Delete a specific product image</summary>
    /// <remarks>
    /// Removes one image from a product's gallery. The image ID is found in the product's `images` array
    /// when you call `GET /api/v1/admin/products/{id}`.
    ///
    /// ⚠️ The image file in Firebase Storage is NOT deleted — only the database record is removed.
    /// </remarks>
    /// <param name="id">Product ID</param>
    /// <param name="imageId">Image record ID to delete (get this from the product detail response)</param>
    [HttpDelete("{id:int}/images/{imageId:int}")]
    public async Task<IActionResult> DeleteImage(int id, int imageId)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var img = await _db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == id);
        if (img == null) return NotFound(new { success = false, message = "Image not found." });
        _db.ProductImages.Remove(img);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Image deleted." });
    }

    // ─── Delete product ───────────────────────────────────────────────────

    /// <summary>Delete a product permanently</summary>
    /// <remarks>
    /// Permanently deletes the product and all its related records (flat data, attribute values, inventory, price index, image records).
    ///
    /// ⚠️ This cannot be undone. Image files in Firebase Storage are NOT deleted.
    ///
    /// **Tip:** Instead of deleting, consider setting `status = false` to hide the product from the app while keeping the data.
    /// </remarks>
    /// <param name="id">Product ID to delete</param>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound(new { success = false, message = "Product not found." });

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Product deleted." });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private async Task<AttrMap> GetAttrMapAsync()
    {
        if (_attrs != null) return _attrs;
        await _attrLock.WaitAsync();
        try
        {
            if (_attrs != null) return _attrs;
            var codes = new[] { "name", "sku", "url_key", "description", "short_description",
                                 "price", "special_price", "status", "weight", "featured", "new" };
            var list = await _db.Attributes
                .Where(a => codes.Contains(a.Code))
                .Select(a => new { a.Code, a.Id })
                .ToListAsync();
            int Get(string code) => list.FirstOrDefault(a => a.Code == code)?.Id ?? 0;
            _attrs = new AttrMap(
                Name:             Get("name"),
                Sku:              Get("sku"),
                UrlKey:           Get("url_key"),
                Description:      Get("description"),
                ShortDescription: Get("short_description"),
                Price:            Get("price"),
                SpecialPrice:     Get("special_price"),
                Status:           Get("status"),
                Weight:           Get("weight"),
                Featured:         Get("featured"),
                New:              Get("new"));
            return _attrs;
        }
        finally { _attrLock.Release(); }
    }

    private static List<ProductAttributeValue> BuildAttributeValues(
        int productId, AttrMap a,
        string sku, string name, string urlKey,
        string? description, string? shortDescription,
        decimal price, decimal? specialPrice, decimal? weight,
        bool status, bool featured, bool isNew,
        string locale, string channel)
    {
        var list = new List<ProductAttributeValue>();

        void AddText(int attrId, string? value, bool scoped = false) {
            if (attrId == 0 || value == null) return;
            list.Add(new ProductAttributeValue {
                ProductId   = productId,
                AttributeId = attrId,
                TextValue   = value,
                Locale      = scoped ? locale : null,
                Channel     = null,
                UniqueId    = $"{productId}_{attrId}_{(scoped ? locale : "all")}_all",
            });
        }
        void AddFloat(int attrId, decimal? value) {
            if (attrId == 0 || !value.HasValue) return;
            list.Add(new ProductAttributeValue {
                ProductId   = productId,
                AttributeId = attrId,
                FloatValue  = value,
                UniqueId    = $"{productId}_{attrId}_all_all",
            });
        }
        void AddBool(int attrId, bool value) {
            if (attrId == 0) return;
            list.Add(new ProductAttributeValue {
                ProductId    = productId,
                AttributeId  = attrId,
                BooleanValue = value,
                IntegerValue = value ? 1 : 0,
                UniqueId     = $"{productId}_{attrId}_all_all",
            });
        }

        AddText(a.Name,             name,             scoped: true);
        AddText(a.Sku,              sku);
        AddText(a.UrlKey,           urlKey);
        AddText(a.Description,      description,      scoped: true);
        AddText(a.ShortDescription, shortDescription, scoped: true);
        AddFloat(a.Price,        price);
        AddFloat(a.SpecialPrice, specialPrice);
        AddFloat(a.Weight,       weight);
        AddBool(a.Status,   status);
        AddBool(a.Featured, featured);
        AddBool(a.New,      isNew);
        return list;
    }

    private async Task<List<string>> UploadAndSaveImagesAsync(
        int productId, string sku, IFormFileCollection files)
    {
        var nextPos = await _db.ProductImages
            .Where(i => i.ProductId == productId)
            .MaxAsync(i => (int?)i.Position) ?? 0;

        var urls = new List<string>();
        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            var safeName  = FirebaseStorageService.SanitiseFileName(file.FileName);
            var storagePath = $"products/{sku}/{++nextPos}_{safeName}";
            await using var stream = file.OpenReadStream();
            var (url, err) = await _storage.UploadFromStreamAsync(stream, storagePath, file.ContentType);
            if (err != null) continue;

            _db.ProductImages.Add(new ProductImage
            {
                ProductId = productId,
                Path      = url!,
                Position  = nextPos,
            });
            urls.Add(url!);
        }
        await _db.SaveChangesAsync();
        return urls;
    }

    private static List<int> ParseIntList(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(s => int.TryParse(s, out var n) ? n : 0)
           .Where(n => n > 0)
           .ToList();

    private static object FormatProduct(Product p, bool detail = false)
    {
        var flat  = p.Flats.FirstOrDefault(f => f.Locale == "en") ?? p.Flats.FirstOrDefault();
        var inv   = p.Inventories.FirstOrDefault();
        var imgs  = p.Images.OrderBy(i => i.Position).Select(i => i.Path).ToList();
        var cats  = p.Categories.Select(c => new {
            id   = c.Id,
            name = c.Translations.FirstOrDefault(t => t.Locale == "en")?.Name
                   ?? c.Translations.FirstOrDefault()?.Name ?? "",
        }).ToList();

        return new
        {
            id                = p.Id,
            sku               = p.Sku,
            type              = p.Type,
            name              = flat?.Name,
            url_key           = flat?.UrlKey,
            short_description = detail ? flat?.ShortDescription : null,
            description       = detail ? flat?.Description : null,
            price             = flat?.Price,
            special_price     = flat?.SpecialPrice,
            weight            = flat?.Weight,
            status            = flat?.Status,
            featured          = flat?.Featured,
            is_new            = flat?.New,
            stock_qty         = inv?.Qty,
            meta_title        = detail ? flat?.MetaTitle : null,
            meta_keywords     = detail ? flat?.MetaKeywords : null,
            meta_description  = detail ? flat?.MetaDescription : null,
            images            = imgs,
            categories        = cats,
            created_at        = p.CreatedAt,
            updated_at        = p.UpdatedAt,
        };
    }
}
