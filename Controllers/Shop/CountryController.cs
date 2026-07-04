using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Services;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/countries")]
[Tags("Country")]
[AllowAnonymous]
public class ShopCountryController : ControllerBase
{
    private readonly DOSDbContext _db;
    private readonly string _locale;

    public ShopCountryController(DOSDbContext db, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
    }

    /// <summary>Get all countries</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var countries = await _db.Countries
            .Include(c => c.Translations)
            .Include(c => c.States)
            .OrderBy(c => c.Name)
            .ToListAsync();

        return Ok(countries.Select(c => new
        {
            c.Id,
            c.Code,
            Name = c.Translations.FirstOrDefault(t => t.Locale == _locale)?.Name ?? c.Name,
            States = c.States.Select(s => new
            {
                s.Id,
                s.CountryCode,
                s.Code,
                s.DefaultName,
                s.CountryId
            }).ToList(),
            Translations = new List<object>()
        }).ToList());
    }

    /// <summary>Get a single country by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var c = await _db.Countries
            .Include(c => c.Translations)
            .Include(c => c.States)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return NotFound(new { message = "Country not found." });

        return Ok(new
        {
            c.Id,
            c.Code,
            Name = c.Translations.FirstOrDefault(t => t.Locale == _locale)?.Name ?? c.Name,
            States = c.States.Select(s => new
            {
                s.Id,
                s.CountryCode,
                s.Code,
                s.DefaultName,
                s.CountryId
            }).ToList(),
            Translations = new List<object>()
        });
    }
}
