using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Services;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop")]
[Tags("CountryState")]
public class ShopCountryStateController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly string _locale;

    public ShopCountryStateController(BagistoDbContext db, LocaleContext localeCtx)
    {
        _db = db;
        _locale = localeCtx.Locale;
    }

    /// <summary>Get all country states</summary>
    [HttpGet("country-states")]
    public async Task<IActionResult> GetAll()
    {
        var states = await _db.CountryStates
            .Include(s => s.Translations)
            .OrderBy(s => s.DefaultName)
            .ToListAsync();

        return Ok(states.Select(MapState).ToList());
    }

    /// <summary>Get a single country state by ID</summary>
    [HttpGet("country-states/{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var s = await _db.CountryStates
            .Include(s => s.Translations)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (s == null) return NotFound(new { message = "Country state not found." });

        return Ok(MapState(s));
    }

    /// <summary>Get states for a specific country</summary>
    [HttpGet("countries/{countryId:int}/states")]
    public async Task<IActionResult> GetByCountry(int countryId)
    {
        var country = await _db.Countries.FindAsync(countryId);
        if (country == null) return NotFound(new { message = "Country not found." });

        var states = await _db.CountryStates
            .Include(s => s.Translations)
            .Where(s => s.CountryId == countryId)
            .OrderBy(s => s.DefaultName)
            .ToListAsync();

        return Ok(states.Select(MapState).ToList());
    }

    /// <summary>Get a specific state for a country</summary>
    [HttpGet("countries/{countryId:int}/states/{id:int}")]
    public async Task<IActionResult> GetOneByCountry(int countryId, int id)
    {
        var country = await _db.Countries.FindAsync(countryId);
        if (country == null) return NotFound(new { message = "Country not found." });

        var s = await _db.CountryStates
            .Include(s => s.Translations)
            .FirstOrDefaultAsync(s => s.Id == id && s.CountryId == countryId);

        if (s == null) return NotFound(new { message = "Country state not found." });

        return Ok(MapState(s));
    }

    private object MapState(Models.CountryState s)
    {
        return new
        {
            s.Id,
            s.CountryCode,
            s.Code,
            DefaultName = s.DefaultName,
            s.CountryId,
            Translations = s.Translations.Select(t => new
            {
                t.Id,
                t.CountryStateId,
                t.Locale,
                t.DefaultName
            }).ToList()
        };
    }
}
