using BagistoApi.Data;
using Microsoft.AspNetCore.Mvc;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SeedController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public SeedController(BagistoDbContext db) => _db = db;

    /// <summary>
    /// Seeds Digital Darsi catalog data from Data/scraped_data.json into
    /// the database. Idempotent: skips when already seeded unless
    /// forceReseed=true is passed (which wipes the previous v2 data first).
    /// </summary>
    [HttpPost("digital-darsi")]
    public async Task<IActionResult> SeedDigitalDarsi([FromQuery] bool forceReseed = false)
    {
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
