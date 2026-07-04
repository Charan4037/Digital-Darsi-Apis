using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;

namespace DOSApi.Controllers.Shop;

[ApiController]
[Route("api/shop/attributes")]
[Tags("Attribute")]
[AllowAnonymous]
public class ShopAttributeController : ControllerBase
{
    private readonly DOSDbContext _db;

    public ShopAttributeController(DOSDbContext db)
    {
        _db = db;
    }

    /// <summary>List all attributes with translations and options</summary>
    [HttpGet]
    public async Task<IActionResult> GetAttributes()
    {
        var attributes = await _db.Attributes
            .Include(a => a.Translations)
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .OrderBy(a => a.Id)
            .ToListAsync();

        return Ok(attributes.Select(MapAttribute).ToList());
    }

    /// <summary>Get a single attribute by ID</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAttribute(int id)
    {
        var a = await _db.Attributes
            .Include(a => a.Translations)
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (a == null) return NotFound(new { message = "Attribute not found." });

        return Ok(MapAttribute(a));
    }

    private object MapAttribute(Models.Catalog.Attribute a)
    {
        // Derive columnName from type
        var columnName = a.Type switch
        {
            "text" => "text_value",
            "textarea" => "text_value",
            "boolean" => "boolean_value",
            "select" => "integer_value",
            "multiselect" => "text_value",
            "datetime" => "datetime_value",
            "date" => "date_value",
            "price" => "float_value",
            "image" => "text_value",
            "file" => "text_value",
            "checkbox" => "text_value",
            _ => "text_value"
        };

        // Build validations string
        var validations = "";
        if (a.IsRequired)
            validations = "{ required: true }";
        if (!string.IsNullOrEmpty(a.Validation))
            validations = string.IsNullOrEmpty(validations) ? a.Validation : validations;

        return new
        {
            a.Id,
            a.Code,
            a.AdminName,
            a.Type,
            a.Position,
            IsRequired = a.IsRequired ? 1 : 0,
            IsUnique = a.IsUnique ? 1 : 0,
            IsFilterable = a.IsFilterable ? 1 : 0,
            IsComparable = a.IsComparable ? 1 : 0,
            IsConfigurable = a.IsConfigurable ? 1 : 0,
            IsUserDefined = a.IsUserDefined ? 1 : 0,
            IsVisibleOnFront = a.IsVisibleOnFront ? 1 : 0,
            ValuePerLocale = a.ValuePerLocale ? 1 : 0,
            ValuePerChannel = a.ValuePerChannel ? 1 : 0,
            EnableWysiwyg = a.EnableWysiwyg ? 1 : 0,
            a.CreatedAt,
            a.UpdatedAt,
            ColumnName = columnName,
            Validations = validations,
            Options = a.Options.Select(o => new
            {
                o.Id,
                o.AdminName,
                o.SortOrder,
                o.SwatchValue,
                AttributeId = o.AttributeId,
                Translations = o.Translations.Select(ot => new
                {
                    ot.Id,
                    ot.Locale,
                    ot.Label,
                    AttributeOptionId = ot.AttributeOptionId
                }).ToList()
            }).ToList(),
            Translations = a.Translations.Select(t => $"/api/attribute_translations/{t.Id}").ToList()
        };
    }
}
