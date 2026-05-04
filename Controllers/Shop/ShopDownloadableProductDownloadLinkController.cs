using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/downloadable-product-download-links")]
[Tags("DownloadableProductDownloadLink")]
[Authorize]
public class ShopDownloadableProductDownloadLinkController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopDownloadableProductDownloadLinkController(BagistoDbContext db)
    {
        _db = db;
    }

    private int GetCustomerId() =>
        int.Parse(User.FindFirst("customerId")?.Value ?? "0");

    // ── Map download link to response shape ──────────────────────────
    private static object MapDownloadLink(Models.Sales.DownloadableLinkPurchased d)
    {
        return new
        {
            id = d.Id,
            product_name = d.ProductName,
            name = d.Name,
            url = d.Url,
            file = d.File,
            file_name = d.FileName,
            type = d.Type,
            download_bought = d.DownloadBought,
            download_used = d.DownloadUsed,
            download_canceled = d.DownloadCanceled,
            status = d.Status,
            customer_id = d.CustomerId,
            order_id = d.OrderId,
            order_item_id = d.OrderItemId,
            created_at = d.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            updated_at = d.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    /// <summary>List download links for purchased products</summary>
    [HttpGet]
    public async Task<IActionResult> GetDownloadLinks()
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var items = await _db.DownloadableLinkPurchased
            .Where(d => d.CustomerId == customerId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();

        var data = items.Select(MapDownloadLink);

        return Ok(data);
    }

    /// <summary>Get a single download link</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetDownloadLink(int id)
    {
        var customerId = GetCustomerId();
        if (customerId == 0) return Unauthorized();

        var d = await _db.DownloadableLinkPurchased
            .FirstOrDefaultAsync(d => d.Id == id && d.CustomerId == customerId);

        if (d == null) return NotFound(new { message = "Download link not found." });

        return Ok(MapDownloadLink(d));
    }
}
