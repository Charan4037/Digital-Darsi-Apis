using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/locales")]
[Tags("Locale")]
[AllowAnonymous]
public class ShopLocaleController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly string _baseUrl;

    public ShopLocaleController(BagistoDbContext db, IConfiguration config)
    {
        _db = db;
        _baseUrl = (config["App:BaseUrl"] ?? "http://localhost:8000").TrimEnd('/');
    }

    /// <summary>Get all locales</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var locales = await _db.Locales
            .OrderBy(l => l.Name)
            .ToListAsync();

        return Ok(locales.Select(MapLocale).ToList());
    }

    /// <summary>Get a single locale by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var l = await _db.Locales.FindAsync(id);
        if (l == null) return NotFound(new { message = "Locale not found." });

        return Ok(MapLocale(l));
    }

    private object MapLocale(Models.Locale l)
    {
        return new
        {
            l.Id,
            l.Code,
            l.Name,
            l.Direction,
            l.LogoPath,
            LogoUrl = l.LogoPath != null ? $"{_baseUrl}/storage/{l.LogoPath}" : null
        };
    }
}
