using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/attribute_translations")]
[Tags("AttributeTranslation")]
[AllowAnonymous]
public class ShopAttributeTranslationController : ControllerBase
{
    private readonly BagistoDbContext _db;

    public ShopAttributeTranslationController(BagistoDbContext db)
    {
        _db = db;
    }

    /// <summary>List all attribute translations</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var translations = await _db.AttributeTranslations
            .OrderBy(t => t.Id)
            .ToListAsync();

        return Ok(translations.Select(t => new
        {
            t.Id,
            t.AttributeId,
            t.Locale,
            t.Name
        }).ToList());
    }

    /// <summary>Get a single attribute translation by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var t = await _db.AttributeTranslations.FindAsync(id);
        if (t == null) return NotFound(new { message = "Attribute translation not found." });

        return Ok(new
        {
            t.Id,
            t.AttributeId,
            t.Locale,
            t.Name
        });
    }

    /// <summary>Create a new attribute translation</summary>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateAttributeTranslationRequest request)
    {
        var attribute = await _db.Attributes.FindAsync(request.AttributeId);
        if (attribute == null) return NotFound(new { message = "Attribute not found." });

        var translation = new Models.Catalog.AttributeTranslation
        {
            AttributeId = request.AttributeId,
            Locale = request.Locale,
            Name = request.Name
        };

        _db.AttributeTranslations.Add(translation);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            translation.Id,
            translation.AttributeId,
            translation.Locale,
            translation.Name
        });
    }

    /// <summary>Update an attribute translation</summary>
    [HttpPatch("{id:int}")]
    [Authorize]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAttributeTranslationRequest request)
    {
        var translation = await _db.AttributeTranslations.FindAsync(id);
        if (translation == null) return NotFound(new { message = "Attribute translation not found." });

        if (request.Name != null)
            translation.Name = request.Name;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            translation.Id,
            translation.AttributeId,
            translation.Locale,
            translation.Name
        });
    }

    /// <summary>Delete an attribute translation</summary>
    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var translation = await _db.AttributeTranslations.FindAsync(id);
        if (translation == null) return NotFound(new { message = "Attribute translation not found." });

        _db.AttributeTranslations.Remove(translation);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Attribute translation deleted successfully." });
    }
}

public class CreateAttributeTranslationRequest
{
    public int AttributeId { get; set; }
    public string Locale { get; set; } = "";
    public string? Name { get; set; }
}

public class UpdateAttributeTranslationRequest
{
    public string? Name { get; set; }
}
