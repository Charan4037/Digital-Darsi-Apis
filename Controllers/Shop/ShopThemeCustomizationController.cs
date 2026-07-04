using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/theme-customizations")]
[Tags("ThemeCustomization")]
[AllowAnonymous]
public class ShopThemeCustomizationController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopThemeCustomizationController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List theme customizations, supports ?type= filter</summary>
    [HttpGet]
    public async Task<IActionResult> GetThemeCustomizations([FromQuery] string? type = null)
    {
        var query = _db.ThemeCustomizations
            .Include(tc => tc.Translations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(tc => tc.Type == type);

        var customizations = await query
            .OrderBy(tc => tc.SortOrder)
            .ToListAsync();

        var data = customizations.Select(tc => new
        {
            tc.Id,
            tc.Name,
            tc.Type,
            tc.SortOrder,
            Status = tc.Status ? 1 : 0,
            tc.ThemeCode,
            tc.ChannelId,
            tc.CreatedAt,
            tc.UpdatedAt,
            Translations = tc.Translations.Select(t => $"/api/shop/theme_customization_translations/{t.Id}").ToList()
        }).ToList();

        return Ok(data);
    }

    /// <summary>Get a single theme customization</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetThemeCustomization(int id)
    {
        var tc = await _db.ThemeCustomizations
            .Include(tc => tc.Translations)
            .FirstOrDefaultAsync(tc => tc.Id == id);

        if (tc == null) return NotFound(new { message = "Theme customization not found." });

        return Ok(new
        {
            tc.Id,
            tc.Name,
            tc.Type,
            tc.SortOrder,
            Status = tc.Status ? 1 : 0,
            tc.ThemeCode,
            tc.ChannelId,
            tc.CreatedAt,
            tc.UpdatedAt,
            Translations = tc.Translations.Select(t => $"/api/shop/theme_customization_translations/{t.Id}").ToList()
        });
    }
}
