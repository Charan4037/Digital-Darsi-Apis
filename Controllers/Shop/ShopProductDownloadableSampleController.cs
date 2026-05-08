using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/product-downloadable-samples")]
[Tags("ProductDownloadableSample")]
[AllowAnonymous]
public class ShopProductDownloadableSampleController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopProductDownloadableSampleController(BagistoDbContext db)
    {
        _db = db;
    }

    /// <summary>List all product downloadable samples</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? product_id = null)
    {
        var query = _db.ProductDownloadableSamples.AsQueryable();

        if (product_id.HasValue)
            query = query.Where(s => s.ProductId == product_id);

        var items = await query
            .OrderBy(s => s.SortOrder)
            .ToListAsync();

        var data = items.Select(s => new
        {
            s.Id,
            s.ProductId,
            s.Url,
            s.File,
            s.FileName,
            s.Type,
            s.SortOrder,
            s.CreatedAt,
            s.UpdatedAt
        }).ToList();

        return Ok(data);
    }

    /// <summary>Get a single product downloadable sample by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var s = await _db.ProductDownloadableSamples.FindAsync(id);
        if (s == null) return NotFound(new { message = "Product downloadable sample not found." });

        return Ok(new
        {
            s.Id,
            s.ProductId,
            s.Url,
            s.File,
            s.FileName,
            s.Type,
            s.SortOrder,
            s.CreatedAt,
            s.UpdatedAt
        });
    }
}
