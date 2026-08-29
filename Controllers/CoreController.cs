using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers;

[ApiController]
[Route("api/v1")]
[Tags("Core")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class CoreController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly string _locale;
    private readonly string _baseUrl;

    public CoreController(DOSDbContext db, IConfiguration config, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
    }

    // ─── Locales ────────────────────────────────────────────────────────

    /// <summary>Get available locales</summary>
    [HttpGet("core/locales")]
    public async Task<IActionResult> GetLocales()
    {
        var locales = await _db.Locales.OrderBy(l => l.Name).ToListAsync();
        return Ok(new { data = locales.Select(l => new { l.Id, l.Code, l.Name, l.Direction }) });
    }

    // ─── Channels ───────────────────────────────────────────────────────

    /// <summary>Get store channels</summary>
    [HttpGet("core/channels")]
    public async Task<IActionResult> GetChannels()
    {
        var channels = await _db.Channels
            .AsSplitQuery()
            .Include(c => c.Translations)
            .Include(c => c.Locales)
            .Include(c => c.Currencies)
            .ToListAsync();

        return Ok(new
        {
            data = channels.Select(c =>
            {
                var t = c.Translations.FirstOrDefault(t => t.Locale == _locale) ?? c.Translations.FirstOrDefault();
                return new
                {
                    c.Id,
                    c.Code,
                    Name = t?.Name ?? c.Code,
                    Description = t?.Description,
                    c.Hostname,
                    LogoUrl = c.Logo != null ? $"{_baseUrl}/storage/{c.Logo}" : null,
                    FaviconUrl = c.Favicon != null ? $"{_baseUrl}/storage/{c.Favicon}" : null,
                    Locales = c.Locales.Select(l => new { l.Id, l.Code, l.Name }),
                    Currencies = c.Currencies.Select(cur => new { cur.Id, cur.Code, cur.Name })
                };
            })
        });
    }

    // ─── CMS Pages ──────────────────────────────────────────────────────

    /// <summary>Get CMS pages</summary>
    [HttpGet("cms/pages")]
    [Tags("CMS")]
    public async Task<IActionResult> GetCmsPages()
    {
        var pages = await _db.CmsPages.Include(p => p.Translations).ToListAsync();

        return Ok(new
        {
            data = pages.Select(p =>
            {
                var t = p.Translations.FirstOrDefault(t => t.Locale == _locale) ?? p.Translations.FirstOrDefault();
                return new
                {
                    p.Id,
                    p.Layout,
                    Title = t?.PageTitle,
                    UrlKey = t?.UrlKey,
                    HtmlContent = t?.HtmlContent,
                    MetaTitle = t?.MetaTitle,
                    MetaDescription = t?.MetaDescription,
                    MetaKeywords = t?.MetaKeywords,
                    p.CreatedAt
                };
            })
        });
    }

    /// <summary>Get a single CMS page by ID</summary>
    [HttpGet("cms/pages/{id:int}")]
    [Tags("CMS")]
    public async Task<IActionResult> GetCmsPage(int id)
    {
        var page = await _db.CmsPages.Include(p => p.Translations).FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound(new { message = "Page not found." });

        var t = page.Translations.FirstOrDefault(t => t.Locale == _locale) ?? page.Translations.FirstOrDefault();
        return Ok(new
        {
            data = new
            {
                page.Id,
                page.Layout,
                Title = t?.PageTitle,
                UrlKey = t?.UrlKey,
                HtmlContent = t?.HtmlContent,
                MetaTitle = t?.MetaTitle,
                MetaDescription = t?.MetaDescription,
                MetaKeywords = t?.MetaKeywords,
                page.CreatedAt
            }
        });
    }

    // ─── Theme Customizations ───────────────────────────────────────────

    /// <summary>Get theme customizations for storefront</summary>
    [HttpGet("theme/customizations")]
    [Tags("Theme")]
    public async Task<IActionResult> GetThemeCustomizations()
    {
        var themes = await _db.ThemeCustomizations
            .Include(t => t.Translations)
            .Where(t => t.Status)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();

        return Ok(new
        {
            data = themes.Select(t =>
            {
                var tr = t.Translations.FirstOrDefault(tr => tr.Locale == _locale) ?? t.Translations.FirstOrDefault();
                return new
                {
                    t.Id,
                    t.Name,
                    t.Type,
                    t.SortOrder,
                    t.ThemeCode,
                    t.ChannelId,
                    Options = tr?.Options
                };
            })
        });
    }
}
