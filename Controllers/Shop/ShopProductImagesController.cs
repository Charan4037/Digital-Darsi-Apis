using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/images")]
[Tags("ProductImages")]
[AllowAnonymous]
public class ShopProductImagesController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopProductImagesController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List all product images</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? product_id = null)
    {
        var query = _db.ProductImages.AsQueryable();

        if (product_id.HasValue)
            query = query.Where(i => i.ProductId == product_id);

        var images = await query
            .OrderBy(i => i.Position)
            .ToListAsync();

        var data = images.Select(i => new
        {
            i.Id,
            i.Type,
            i.Path,
            i.ProductId,
            i.Position
        }).ToList();

        return Ok(data);
    }
}
