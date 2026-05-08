using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/v1")]
[Tags("Core")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class CoreController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly string _locale;
    private readonly string _baseUrl;

    public CoreController(BagistoDbContext db, IConfiguration config, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
        _baseUrl = config["App:BaseUrl"] ?? "http://192.168.0.116:8000";
    }

    // ─── Countries & States ─────────────────────────────────────────────

    /// <summary>Get list of countries</summary>
    [HttpGet("core/countries")]
    public async Task<IActionResult> GetCountries()
    {
        var countries = await _db.Countries
            .Include(c => c.Translations)
            .OrderBy(c => c.Name)
            .ToListAsync();

        return Ok(new
        {
            data = countries.Select(c => new
            {
                c.Id,
                c.Code,
                Name = c.Translations.FirstOrDefault(t => t.Locale == _locale)?.Name ?? c.Name
            })
        });
    }

    /// <summary>Get states for a country</summary>
    [HttpGet("core/states")]
    public async Task<IActionResult> GetStates([FromQuery] string? countryCode, [FromQuery] int? countryId)
    {
        IQueryable<Models.CountryState> q = _db.CountryStates.Include(s => s.Translations);

        if (!string.IsNullOrEmpty(countryCode))
            q = q.Where(s => s.CountryCode == countryCode);
        else if (countryId.HasValue)
            q = q.Where(s => s.CountryId == countryId);

        var states = await q.OrderBy(s => s.DefaultName).ToListAsync();

        return Ok(new
        {
            data = states.Select(s => new
            {
                s.Id,
                s.Code,
                s.CountryCode,
                Name = s.Translations.FirstOrDefault(t => t.Locale == _locale)?.DefaultName ?? s.DefaultName
            })
        });
    }

    // ─── Locales ────────────────────────────────────────────────────────

    /// <summary>Get available locales</summary>
    [HttpGet("core/locales")]
    public async Task<IActionResult> GetLocales()
    {
        var locales = await _db.Locales.OrderBy(l => l.Name).ToListAsync();
        return Ok(new { data = locales.Select(l => new { l.Id, l.Code, l.Name, l.Direction }) });
    }

    // ─── Currencies ─────────────────────────────────────────────────────

    /// <summary>Get available currencies</summary>
    [HttpGet("core/currencies")]
    public async Task<IActionResult> GetCurrencies()
    {
        var currencies = await _db.Currencies.OrderBy(c => c.Name).ToListAsync();
        return Ok(new { data = currencies.Select(c => new { c.Id, c.Code, c.Name, c.Symbol }) });
    }

    // ─── Channels ───────────────────────────────────────────────────────

    /// <summary>Get store channels</summary>
    [HttpGet("core/channels")]
    public async Task<IActionResult> GetChannels()
    {
        var channels = await _db.Channels
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
