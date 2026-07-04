using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/pages")]
[Tags("Page")]
[AllowAnonymous]
public class ShopPageController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopPageController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List CMS pages with translation IRI references</summary>
    [HttpGet]
    public async Task<IActionResult> GetPages()
    {
        var pages = await _db.CmsPages
            .Include(p => p.Translations)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var data = pages.Select(p => new
        {
            p.Id,
            p.CreatedAt,
            p.UpdatedAt,
            Translation = p.Translations.FirstOrDefault() != null
                ? $"/api/shop/page_translations/{p.Translations.First().Id}"
                : (string?)null,
            Translations = p.Translations.Select(t => $"/api/shop/page_translations/{t.Id}").ToList()
        }).ToList();

        return Ok(data);
    }

    /// <summary>Get a single CMS page</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetPage(int id)
    {
        var p = await _db.CmsPages
            .Include(p => p.Translations)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (p == null) return NotFound(new { message = "Page not found." });

        return Ok(new
        {
            p.Id,
            p.CreatedAt,
            p.UpdatedAt,
            Translation = p.Translations.FirstOrDefault() != null
                ? $"/api/shop/page_translations/{p.Translations.First().Id}"
                : (string?)null,
            Translations = p.Translations.Select(t => $"/api/shop/page_translations/{t.Id}").ToList()
        });
    }
}
