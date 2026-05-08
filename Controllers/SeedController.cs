using BagistoApi.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SeedController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public SeedController(BagistoDbContext db, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db;
        _config = config;
        _env = env;
    }

    /// <summary>
    /// Seeds Digital Darsi catalog data from Data/scraped_data.json into
    /// the database. Idempotent: skips when already seeded unless
    /// forceReseed=true is passed (which wipes the previous v2 data first).
    ///
    /// Disabled by default in non-Development environments unless
    /// <c>Seed:Enabled=true</c> is explicitly set in configuration.
    /// </summary>
    [HttpPost("digital-darsi")]
    public async Task<IActionResult> SeedDigitalDarsi([FromQuery] bool forceReseed = false)
    {
        var explicitlyEnabled = string.Equals(_config["Seed:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
        if (!_env.IsDevelopment() && !explicitlyEnabled)
            return NotFound();

        try
        {
            await DigitalDarsiSeeder.SeedAsync(_db, forceReseed);
            return Ok(new { message = "Digital Darsi data seeded successfully!", forceReseed });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = ex.Message,
                details = ex.InnerException?.Message,
                stack = ex.StackTrace,
            });
        }
    }
}
