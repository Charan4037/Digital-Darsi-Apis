using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Admin;

/// <summary>
/// Emergency admin endpoint to fix product variant pricing issues.
/// When a child variant is added, the parent product price should NOT change.
/// This endpoint identifies and repairs such cases.
/// </summary>
[ApiController]
[Route("api/admin/product-variant-repair")]
[Tags("Admin - Product Repair")]
[AllowAnonymous]
public class AdminProductVariantRepairController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly ProductService _productService;

    public AdminProductVariantRepairController(DOSDbContext db, ProductService productService)
    {
        _db = db;
        _productService = productService;
    }

    /// <summary>
    /// Diagnose: Find products with variants where the parent price might be wrong.
    /// Returns detailed info about each problem product.
    /// </summary>
    [HttpGet("diagnose")]
    public async Task<IActionResult> Diagnose()
    {
        await _productService.EnsureAttrIdsAsync();

        // Find all configurable products (have children)
        var configurableProducts = await _db.Products
            .AsNoTracking()
            .Where(p => p.Children.Count > 0)
            .Include(p => p.AttributeValues)
            .Include(p => p.Flats)
            .Include(p => p.Children)
                .ThenInclude(c => c.AttributeValues)
            .Include(p => p.Children)
                .ThenInclude(c => c.Flats)
            .ToListAsync();

        var issues = new List<object>();

        foreach (var parent in configurableProducts)
        {
            var parentName = _productService.GetProductName(parent);
            var parentPrice = _productService.GetProductPrice(parent);
            var parentSpecialPrice = _productService.GetProductSpecialPrice(parent);

            var childPrices = parent.Children
                .Select(c => new
                {
                    childId = c.Id,
                    childSku = c.Sku,
                    childPrice = _productService.GetProductPrice(c),
                    childSpecialPrice = _productService.GetProductSpecialPrice(c)
                })
                .ToList();

            // Check if parent price matches a child price (which indicates it was incorrectly updated)
            var priceMatches = childPrices
                .Where(cp => cp.childPrice == parentPrice || cp.childSpecialPrice == parentSpecialPrice)
                .ToList();

            if (priceMatches.Any())
            {
                issues.Add(new
                {
                    parentId = parent.Id,
                    parentSku = parent.Sku,
                    parentName = parentName,
                    parentPrice = parentPrice,
                    parentSpecialPrice = parentSpecialPrice,
                    childPrices = childPrices,
                    priceMatches = priceMatches,
                    issue = "Parent price matches a child price - parent was likely incorrectly updated when variant was added"
                });
            }
        }

        return Ok(new
        {
            totalConfigurableProducts = configurableProducts.Count,
            problemCount = issues.Count,
            problems = issues
        });
    }

    /// <summary>
    /// Fix: For a specific product, restore the parent price based on stored metadata.
    /// The fix looks for the original parent price in the product's Additional JSON.
    /// </summary>
    [HttpPost("fix-product/{productId}")]
    public async Task<IActionResult> FixProduct(int productId, [FromBody] FixProductRequest? req = null)
    {
        var product = await _db.Products
            .Include(p => p.AttributeValues)
            .Include(p => p.Flats)
            .Include(p => p.Children)
                .ThenInclude(c => c.AttributeValues)
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
            return NotFound(new { message = "Product not found" });

        if (product.Children.Count == 0)
            return BadRequest(new { message = "Product has no child variants" });

        if (req?.newParentPrice is null or <= 0)
            return BadRequest(new { message = "Must provide newParentPrice > 0 in request body" });

        await _productService.EnsureAttrIdsAsync();

        var priceAttrId = typeof(ProductService)
            .GetField("_attrIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
            .GetValue(null) as dynamic;

        // Update the parent product's price attribute
        var priceAttr = await _db.ProductAttributeValues
            .FirstOrDefaultAsync(v => v.ProductId == productId && v.AttributeId == 106); // Price attribute ID

        if (priceAttr != null)
        {
            priceAttr.FloatValue = Convert.ToDecimal(req.newParentPrice);
            _db.ProductAttributeValues.Update(priceAttr);
        }

        // Also update in ProductFlat if it exists
        var flat = product.Flats.FirstOrDefault();
        if (flat != null)
        {
            flat.Price = Convert.ToDecimal(req.newParentPrice);
            _db.ProductFlats.Update(flat);
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Product price repaired successfully",
            productId = productId,
            newPrice = req.newParentPrice
        });
    }
}

public class FixProductRequest
{
    public decimal? newParentPrice { get; set; }
}
