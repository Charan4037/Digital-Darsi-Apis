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

            // Product urlKeys are resolved in one batched query rather than
            // per-row, since the app navigates products by slug (urlKey), not
            // by numeric id — see ProductDetailsScreen on the client. Grouping
            // happens in memory (row counts here are tiny — a handful of
            // banners) rather than as an order-then-group-by-first query,
            // which doesn't reliably translate through the MySQL provider.
            var productIds = rows.Where(b => b.ProductId.HasValue).Select(b => b.ProductId!.Value).Distinct().ToList();
            var urlKeysByProductId = new Dictionary<int, string?>();
            if (productIds.Count > 0)
            {
                var flats = await _db.ProductFlats
                    .Where(f => productIds.Contains(f.ProductId))
                    .Select(f => new { f.ProductId, f.Locale, f.UrlKey })
                    .ToListAsync();
                foreach (var group in flats.GroupBy(f => f.ProductId))
                {
                    var best = group.FirstOrDefault(f => f.Locale == "en") ?? group.First();
                    urlKeysByProductId[group.Key] = best.UrlKey;
                }
            }

            var data = rows.Select(b =>
            {
                // Null/empty link_type is legacy data saved before this field
                // existed — derive it from link_url the same way this endpoint
                // always has, so old banners keep working unchanged.
                var linkType = string.IsNullOrWhiteSpace(b.LinkType)
                    ? (string.IsNullOrWhiteSpace(b.LinkUrl) ? "none" : "url")
                    : b.LinkType;

                string? linkRef = linkType switch
                {
                    "category" => b.CategoryId?.ToString(),
                    "product"  => b.ProductId.HasValue ? urlKeysByProductId.GetValueOrDefault(b.ProductId.Value) : null,
                    "vendor"   => b.VendorId?.ToString(),
                    "url"      => b.LinkUrl,
                    _          => null,
                };

                return new
                {
                    b.Id,
                    placement = "home",
                    title = b.Title,
                    subtitle = b.Subtitle,
                    imageUrl = b.ImageUrl,
                    linkType,
                    linkRef,
                    sortOrder = b.SortOrder,
                    isActive = true,
                };
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
