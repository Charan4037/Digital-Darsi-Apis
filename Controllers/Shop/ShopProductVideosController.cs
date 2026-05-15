using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/videos")]
[Tags("ProductVideos")]
[AllowAnonymous]
public class ShopProductVideosController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopProductVideosController(BagistoDbContext db)
    {
        _db = db;
    }

    /// <summary>List all product videos</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? product_id = null)
    {
        var query = _db.ProductVideos.AsQueryable();

        if (product_id.HasValue)
            query = query.Where(v => v.ProductId == product_id);

        var videos = await query
            .OrderBy(v => v.Position)
            .ToListAsync();

        var data = videos.Select(v => new
        {
            v.Id,
            v.Type,
            v.Path,
            v.ProductId,
            v.Position
        }).ToList();

        return Ok(data);
    }
}
