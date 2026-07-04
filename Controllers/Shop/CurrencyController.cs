using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/currencies")]
[Tags("Currency")]
[AllowAnonymous]
public class ShopCurrencyController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopCurrencyController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>Get all currencies</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var currencies = await _db.Currencies
            .OrderBy(c => c.Name)
            .ToListAsync();

        return Ok(currencies.Select(c => new
        {
            c.Id,
            c.Code,
            c.Name,
            c.Symbol,
            c.Decimal,
            c.GroupSeparator,
            c.DecimalSeparator
        }).ToList());
    }

    /// <summary>Get a single currency by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var c = await _db.Currencies.FindAsync(id);
        if (c == null) return NotFound(new { message = "Currency not found." });

        return Ok(new
        {
            c.Id,
            c.Code,
            c.Name,
            c.Symbol,
            c.Decimal,
            c.GroupSeparator,
            c.DecimalSeparator
        });
    }
}
