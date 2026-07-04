using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/page_translations")]
[Tags("PageTranslation")]
[AllowAnonymous]
public class ShopPageTranslationController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopPageTranslationController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List all CMS page translations</summary>
    [HttpGet]
    public async Task<IActionResult> GetPageTranslations()
    {
        var translations = await _db.CmsPageTranslations
            .OrderBy(t => t.CmsPageId)
            .ToListAsync();

        var data = translations.Select(t => new
        {
            t.Id,
            t.CmsPageId,
            t.PageTitle,
            t.UrlKey,
            t.HtmlContent,
            t.MetaTitle,
            t.MetaDescription,
            t.MetaKeywords,
            t.Locale
        }).ToList();

        return Ok(data);
    }

    /// <summary>Get a single CMS page translation</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetPageTranslation(int id)
    {
        var t = await _db.CmsPageTranslations.FindAsync(id);
        if (t == null) return NotFound(new { message = "Page translation not found." });

        return Ok(new
        {
            t.Id,
            t.CmsPageId,
            t.PageTitle,
            t.UrlKey,
            t.HtmlContent,
            t.MetaTitle,
            t.MetaDescription,
            t.MetaKeywords,
            t.Locale
        });
    }
}
