using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/product-bundle-option-products")]
[Tags("ProductBundleOptionProduct")]
public class ShopProductBundleOptionProductController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopProductBundleOptionProductController(BagistoDbContext db)
    {
        _db = db;
    }

    /// <summary>List all product bundle option products</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var items = await _db.ProductBundleOptionProducts
            .OrderBy(b => b.SortOrder)
            .ToListAsync();

        var data = items.Select(b => new
        {
            b.Id,
            b.ProductBundleOptionId,
            b.ProductId,
            b.Qty,
            IsUserDefined = b.IsUserDefined ? 1 : 0,
            b.SortOrder,
            IsDefault = b.IsDefault ? 1 : 0
        }).ToList();

        return Ok(data);
    }

    /// <summary>Get a single product bundle option product by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var b = await _db.ProductBundleOptionProducts.FindAsync(id);
        if (b == null) return NotFound(new { message = "Product bundle option product not found." });

        return Ok(new
        {
            b.Id,
            b.ProductBundleOptionId,
            b.ProductId,
            b.Qty,
            IsUserDefined = b.IsUserDefined ? 1 : 0,
            b.SortOrder,
            IsDefault = b.IsDefault ? 1 : 0
        });
    }
}
