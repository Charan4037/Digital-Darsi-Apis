using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers;

/// <summary>
/// Serves the homepage promotional banners the scraper captured from each
/// storefront's slider (the <c>dd_scraped_banners</c> staging table). The
/// app's Home carousel reads <c>GET /api/v1/banners?store=&lt;slug&gt;</c>.
/// </summary>
[ApiController]
[Route("api/v1/banners")]
[Tags("Banners")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class BannersController : ControllerBase
{
    private readonly DOSDbContext _db;

    public BannersController(DOSDbContext db) => _db = db;

    /// <summary>
    /// Promotional banners for one store. <paramref name="store"/> accepts the
    /// category slug (<c>dd-foodstore</c>) or the bare site key
    /// (<c>foodstore</c>); omit it to get every store's banners.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetBanners(
        [FromQuery] string placement = "home",
        [FromQuery] string? store = null)
    {
        try
        {
            var siteKey = string.IsNullOrWhiteSpace(store)
                ? null
                : (store.StartsWith("dd-") ? store[3..] : store);

            var q = _db.ScrapedBanners.AsNoTracking();
            if (siteKey != null)
                q = q.Where(b => b.SiteKey == siteKey);

            var rows = await q
                .OrderBy(b => b.SiteKey)
                .ThenBy(b => b.SortOrder)
                .ToListAsync();

            var data = rows.Select(b => new
            {
                b.Id,
                placement = "home",
                title = b.Title,
                subtitle = b.Subtitle,
                imageUrl = b.ImageUrl,
                linkType = string.IsNullOrWhiteSpace(b.LinkUrl) ? "none" : "url",
                linkRef = b.LinkUrl,
                sortOrder = b.SortOrder,
                isActive = true,
            });

            return Ok(new { data });
        }
        catch (Exception)
        {
            // dd_scraped_banners not created yet (scrape_banners.py not run) —
            // degrade gracefully; the app falls back to its static card.
            return Ok(new { data = Array.Empty<object>() });
        }
    }
}
