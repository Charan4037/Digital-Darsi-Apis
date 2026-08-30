using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers;

/// <summary>
/// Serves the home-screen "roadblock" popups — one-time promotional
/// interstitials the app shows when it opens (the <c>dd_roadblock_popups</c>
/// table). Global: shown on the home screen regardless of which store
/// section is selected, so unlike <see cref="BannersController"/> there is
/// no per-store filtering. Any number of rows may be active at once — the
/// app shows them one after another, each dismissed before the next
/// appears, so this returns all of them rather than a single winner.
/// </summary>
[ApiController]
[Route("api/v1/roadblock")]
[Tags("Roadblock")]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class RoadblockController : ControllerBase
{
    private readonly DOSDbContext _db;

    public RoadblockController(DOSDbContext db) => _db = db;

    /// <summary>All active roadblock popups, in the order they should be shown.</summary>
    [HttpGet]
    public async Task<IActionResult> GetRoadblocks()
    {
        try
        {
            var popups = await _db.RoadblockPopups
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Id)
                .ToListAsync();

            if (popups.Count == 0)
                return Ok(new { data = Array.Empty<object>() });

            // Product urlKeys are resolved in one batched query rather than
            // per-row, since the app navigates products by slug (urlKey),
            // not by numeric id — same pattern as BannersController.
            var productIds = popups.Where(p => p.ProductId.HasValue).Select(p => p.ProductId!.Value).Distinct().ToList();
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

            var data = popups.Select(popup =>
            {
                var linkType = string.IsNullOrWhiteSpace(popup.LinkType) ? "none" : popup.LinkType;

                string? linkRef = linkType switch
                {
                    "category" => popup.CategoryId?.ToString(),
                    "product" => popup.ProductId.HasValue ? urlKeysByProductId.GetValueOrDefault(popup.ProductId.Value) : null,
                    "vendor" => popup.VendorId?.ToString(),
                    "url" => popup.LinkUrl,
                    _ => null,
                };

                return new
                {
                    popup.Id,
                    imageUrl = popup.ImageUrl,
                    linkType,
                    linkRef,
                };
            });

            return Ok(new { data });
        }
        catch (Exception)
        {
            // dd_roadblock_popups not created yet — degrade gracefully, the
            // app just doesn't show any popups.
            return Ok(new { data = Array.Empty<object>() });
        }
    }
}
