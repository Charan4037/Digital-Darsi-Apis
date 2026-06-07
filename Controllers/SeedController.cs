using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using BagistoApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BagistoApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SeedController : ControllerBase
{
    private readonly BagistoDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly FirebaseStorageService _firebaseStorage;

    public SeedController(
        BagistoDbContext db,
        IConfiguration config,
        IWebHostEnvironment env,
        FirebaseStorageService firebaseStorage)
    {
        _db = db;
        _config = config;
        _env = env;
        _firebaseStorage = firebaseStorage;
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

    /// <summary>
    /// Repairs the category parent/child hierarchy for already-seeded Digital
    /// Darsi categories — links each sub-category under its real parent —
    /// without re-seeding or touching products.
    ///
    /// The API already runs this automatically on every startup (see
    /// Program.cs), so dev and prod stay consistent on the shared database.
    /// This endpoint is an on-demand re-run — e.g. after importing fresh
    /// scraped categories without restarting the process. Requires
    /// authentication; available in every environment. Idempotent.
    /// </summary>
    [HttpPost("relink-categories")]
    public async Task<IActionResult> RelinkCategories()
    {
        try
        {
            var report = await DigitalDarsiSeeder.RelinkCategoryHierarchyAsync(_db);
            return Ok(new { message = "Category hierarchy re-linked.", report });
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

    /// <summary>
    /// Seeds the catalog from the <c>dd_scraped_*</c> staging tables
    /// populated by the Python DigitalDarsiScraper project. This is the
    /// current data source — pass <c>forceReseed=true</c> to wipe any
    /// previously-seeded Digital Darsi catalogue and replace it.
    ///
    /// Disabled by default in non-Development environments unless
    /// <c>Seed:Enabled=true</c> is explicitly set in configuration.
    /// </summary>
    [HttpPost("digital-darsi-staging")]
    public async Task<IActionResult> SeedDigitalDarsiFromStaging([FromQuery] bool forceReseed = false)
    {
        var explicitlyEnabled = string.Equals(_config["Seed:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
        if (!_env.IsDevelopment() && !explicitlyEnabled)
            return NotFound();

        try
        {
            await DigitalDarsiSeeder.SeedFromStagingAsync(_db, forceReseed);
            return Ok(new { message = "Digital Darsi data seeded from staging tables successfully!", forceReseed });
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

    /// <summary>
    /// Downloads every product image and category logo/banner fresh from
    /// the original source URLs, uploads them to Firebase Storage, and
    /// updates the database records with the new Firebase Storage URLs.
    ///
    /// Idempotent: images already pointing at Firebase Storage are skipped.
    /// Re-run to pick up any that failed a previous pass.
    /// </summary>
    [HttpPost("migrate-images")]
    public async Task<IActionResult> MigrateImagesToFirebase(CancellationToken ct)
    {
        try
        {
            var report = await ImageMigrationRunner.RunAsync(
                _db, _firebaseStorage, Console.Out, ct);
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { message = "Migration cancelled by client." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error   = ex.Message,
                details = ex.InnerException?.Message,
            });
        }
    }
}
