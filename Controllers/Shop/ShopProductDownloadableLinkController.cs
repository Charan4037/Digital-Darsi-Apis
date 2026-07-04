using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/product-downloadable-links")]
[Tags("ProductDownloadableLink")]
[AllowAnonymous]
public class ShopProductDownloadableLinkController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopProductDownloadableLinkController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List all product downloadable links</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? product_id = null)
    {
        var query = _db.ProductDownloadableLinks.AsQueryable();

        if (product_id.HasValue)
            query = query.Where(l => l.ProductId == product_id);

        var items = await query
            .OrderBy(l => l.SortOrder)
            .ToListAsync();

        var data = items.Select(l => new
        {
            l.Id,
            l.ProductId,
            l.Url,
            l.File,
            l.FileName,
            l.Type,
            l.Price,
            l.SampleUrl,
            l.SampleFile,
            l.SampleFileName,
            l.SampleType,
            l.SortOrder,
            l.Downloads,
            l.CreatedAt,
            l.UpdatedAt
        }).ToList();

        return Ok(data);
    }

    /// <summary>Get a single product downloadable link by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var l = await _db.ProductDownloadableLinks.FindAsync(id);
        if (l == null) return NotFound(new { message = "Product downloadable link not found." });

        return Ok(new
        {
            l.Id,
            l.ProductId,
            l.Url,
            l.File,
            l.FileName,
            l.Type,
            l.Price,
            l.SampleUrl,
            l.SampleFile,
            l.SampleFileName,
            l.SampleType,
            l.SortOrder,
            l.Downloads,
            l.CreatedAt,
            l.UpdatedAt
        });
    }
}
