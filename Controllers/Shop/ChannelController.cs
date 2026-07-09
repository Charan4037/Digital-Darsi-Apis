using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/channels")]
[Tags("Channel")]
[AllowAnonymous]
public class ShopChannelController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly string _locale;
    private readonly string _baseUrl;

    public ShopChannelController(DOSDbContext db, IConfiguration config, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
        _baseUrl = config["App:BaseUrl"] ?? "http://localhost:8000";
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var channels = await _db.Channels
            .Include(c => c.Translations)
            .OrderBy(c => c.Id)
            .ToListAsync();

        return Ok(channels.Select(MapChannel).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var c = await _db.Channels
            .Include(c => c.Translations)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return NotFound(new { message = "Channel not found." });

        return Ok(MapChannel(c));
    }

    private object MapChannel(Models.Channel c)
    {
        var currentTranslation = c.Translations.FirstOrDefault(t => t.Locale == _locale)
                              ?? c.Translations.FirstOrDefault();

        object? homeSeo = null;
        if (!string.IsNullOrEmpty(c.HomeSeo))
        {
            try { homeSeo = JsonSerializer.Deserialize<object>(c.HomeSeo); }
            catch { homeSeo = c.HomeSeo; }
        }

        return new
        {
            c.Id,
            c.Code,
            c.Theme,
            c.Hostname,
            HomeSeo = homeSeo,
            IsMaintenanceOn = c.IsMaintenanceOn ? 1 : 0,
            c.CreatedAt,
            c.UpdatedAt,
            Translation = currentTranslation != null
                ? $"/api/shop/channel_translations/{currentTranslation.Id}"
                : null,
            Translations = c.Translations.Select(t => $"/api/shop/channel_translations/{t.Id}").ToList()
        };
    }
}
