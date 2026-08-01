using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Admin;

/// <summary>Emergency endpoint to directly fix specific product pricing issues</summary>
[ApiController]
[Route("api/admin/quick-fix")]
[AllowAnonymous]
public class AdminQuickFixController : ControllerBase
{
    private readonly DOSDbContext _db;

    public AdminQuickFixController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>Fix product price by SKU - updates both product_flats and product_attribute_values</summary>
    [HttpPost("fix-product-sku")]
    public async Task<IActionResult> FixProductBySku(string sku, decimal regularPrice, decimal salePrice)
    {
        var product = await _db.Products
            .Where(p => p.Sku == sku)
            .Include(p => p.Flats)
            .Include(p => p.AttributeValues)
            .FirstOrDefaultAsync();

        if (product == null)
            return NotFound(new { message = "Product not found" });

        var flat = product.Flats.FirstOrDefault();
        if (flat == null)
            return BadRequest(new { message = "Product flat not found" });

        // Update product_flats
        flat.Price = regularPrice;
        flat.SpecialPrice = salePrice;
        _db.ProductFlats.Update(flat);

        // Also update attribute values for price and special_price (IDs 77 and 78 for price/special_price)
        var priceAttrs = product.AttributeValues
            .Where(av => av.ProductId == product.Id && (av.AttributeId == 77 || av.AttributeId == 78))
            .ToList();

        foreach (var attr in priceAttrs)
        {
            if (attr.AttributeId == 77) attr.FloatValue = regularPrice;  // price
            if (attr.AttributeId == 78) attr.FloatValue = salePrice;     // special_price
            _db.ProductAttributeValues.Update(attr);
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Product price updated",
            sku = sku,
            regularPrice = regularPrice,
            salePrice = salePrice,
            attributesUpdated = priceAttrs.Count
        });
    }

    /// <summary>Get product pricing by SKU</summary>
    [HttpGet("get-product-sku")]
    public async Task<IActionResult> GetProductBySku(string sku)
    {
        var product = await _db.Products
            .Where(p => p.Sku == sku)
            .Include(p => p.Flats)
            .Include(p => p.Children)
                .ThenInclude(c => c.Flats)
            .FirstOrDefaultAsync();

        if (product == null)
            return NotFound();

        var flat = product.Flats.FirstOrDefault();
        var allFlats = product.Flats.Select(f => new { f.ProductId, f.Locale, f.Price, f.SpecialPrice }).ToList();
        var children = product.Children.Select(c => new
        {
            sku = c.Sku,
            price = c.Flats.FirstOrDefault()?.Price,
            specialPrice = c.Flats.FirstOrDefault()?.SpecialPrice
        }).ToList();

        return Ok(new
        {
            productId = product.Id,
            sku = product.Sku,
            regularPrice = flat?.Price,
            salePrice = flat?.SpecialPrice,
            allFlats = allFlats,
            children = children
        });
    }
}
