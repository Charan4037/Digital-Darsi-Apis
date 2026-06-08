using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Catalog;
using CatalogAttribute = BagistoApi.Models.Catalog.Attribute;

namespace BagistoApi.Controllers.Admin;

/// <summary>
/// Admin read + write for product attributes, attribute families and options.
/// Routes: /api/v1/admin/attributes  |  /api/v1/admin/attribute-families
///
/// The admin UI uses these endpoints to populate dropdowns (e.g. attribute
/// family selector when creating a product, filterable attributes for a
/// category).  Full CRUD for families and options is included so the
/// admin site can manage the catalogue schema without touching the DB directly.
/// </summary>
[Tags("Admin – Attributes")]
public class AdminAttributeController : AdminBaseController
{
    private readonly BagistoDbContext _db;

    public AdminAttributeController(BagistoDbContext db, IConfiguration config) : base(config)
    {
        _db = db;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ATTRIBUTE FAMILIES
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>List all attribute families</summary>
    /// <remarks>
    /// An **Attribute Family** is a template that defines which attributes a product type has.
    /// For example, "Clothing" family → Size, Color, Material. "Electronics" family → Brand, Warranty, Wattage.
    ///
    /// When creating a product you pick a family — the product form then shows only that family's attributes.
    /// Returns all families with their attribute groups and every attribute inside each group.
    ///
    /// **Use this to:** Populate the "Attribute Family" dropdown when creating a product.
    /// </remarks>
    [HttpGet("api/v1/admin/attribute-families")]
    public async Task<IActionResult> ListFamilies()
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var families = await _db.AttributeFamilies
            .Include(f => f.Groups)
                .ThenInclude(g => g.Attributes)
                    .ThenInclude(a => a.Translations)
            .AsNoTracking()
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data = families.Select(f => new
            {
                id      = f.Id,
                code    = f.Code,
                name    = f.Name,
                status  = f.Status,
                groups  = f.Groups.OrderBy(g => g.Position).Select(g => new
                {
                    id         = g.Id,
                    name       = g.Name,
                    position   = g.Position,
                    attributes = g.Attributes.Select(a => FormatAttribute(a)).ToList(),
                }).ToList(),
            }).ToList()
        });
    }

    /// <summary>Get a single attribute family with all its attributes</summary>
    /// <param name="id">Attribute family ID</param>
    [HttpGet("api/v1/admin/attribute-families/{id:int}")]
    public async Task<IActionResult> GetFamily(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var f = await _db.AttributeFamilies
            .Include(f => f.Groups).ThenInclude(g => g.Attributes).ThenInclude(a => a.Translations)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id);

        if (f == null) return NotFound(new { success = false, message = "Attribute family not found." });
        return Ok(new { success = true, data = new
        {
            id = f.Id, code = f.Code, name = f.Name, status = f.Status,
            groups = f.Groups.OrderBy(g => g.Position).Select(g => new
            {
                id   = g.Id, name = g.Name, position = g.Position,
                attributes = g.Attributes.Select(a => FormatAttribute(a)).ToList(),
            }).ToList()
        }});
    }

    /// <summary>Create a new attribute family</summary>
    /// <remarks>
    /// Creates a new attribute family template.
    ///
    /// **Field guide:**
    /// - `code` — Unique machine key, lowercase, no spaces (e.g. `clothing`, `electronics`). Cannot be changed later
    /// - `name` — Display name shown in admin (e.g. "Clothing", "Electronics")
    /// - `status` — true = active (can be assigned to products). Default: true
    ///
    /// **Example body:**
    /// ```json
    /// { "code": "clothing", "name": "Clothing", "status": true }
    /// ```
    /// </remarks>
    [HttpPost("api/v1/admin/attribute-families")]
    public async Task<IActionResult> CreateFamily([FromBody] CreateFamilyRequest req)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { success = false, message = "code and name are required." });

        if (await _db.AttributeFamilies.AnyAsync(f => f.Code == req.Code))
            return Conflict(new { success = false, message = $"Family code '{req.Code}' already exists." });

        var family = new AttributeFamily
        {
            Code          = req.Code.Trim(),
            Name          = req.Name.Trim(),
            Status        = req.Status,
            IsUserDefined = true,
        };
        _db.AttributeFamilies.Add(family);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Attribute family created.", data = new { family.Id, family.Code, family.Name } });
    }

    public record CreateFamilyRequest(string Code, string Name, bool Status = true);

    // ═══════════════════════════════════════════════════════════════════════
    // ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>List all product attributes</summary>
    /// <remarks>
    /// Returns all attributes defined in the system. An **Attribute** is a product property like "Color", "Size", "Brand", "Weight".
    ///
    /// **Attribute types:**
    /// | type | What it stores | Example |
    /// |---|---|---|
    /// | `text` | Short single-line text | Brand name, model number |
    /// | `textarea` | Long multi-line text | Detailed description |
    /// | `price` | Decimal number for a price | Special price |
    /// | `boolean` | Yes/No toggle | Is returnable, Is featured |
    /// | `select` | Single choice from a list | Color, Size |
    /// | `multiselect` | Multiple choices from a list | Tags, materials |
    /// | `date` | A calendar date | Expiry date |
    /// | `image` | Image upload | Product thumbnail |
    ///
    /// **Filter examples:**
    /// - All select/dropdown attributes: `?type=select`
    /// - Attributes shown in the app's category filter sidebar: `?filterable=true`
    /// - Attributes used to create product variants (e.g. size S/M/L): `?configurable=true`
    /// </remarks>
    /// <param name="type">Filter by data type: text | textarea | price | boolean | select | multiselect | date | image | file</param>
    /// <param name="filterable">true = only attributes shown as filters in the app's category page</param>
    /// <param name="configurable">true = only attributes used for product variants</param>
    [HttpGet("api/v1/admin/attributes")]
    public async Task<IActionResult> ListAttributes(
        [FromQuery] string? type       = null,
        [FromQuery] bool? filterable   = null,
        [FromQuery] bool? configurable = null)
    {
        if (!IsAdmin()) return AdminUnauthorized();

        var query = _db.Attributes
            .Include(a => a.Translations)
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(a => a.Type == type);
        if (filterable.HasValue)              query = query.Where(a => a.IsFilterable == filterable.Value);
        if (configurable.HasValue)            query = query.Where(a => a.IsConfigurable == configurable.Value);

        var attrs = await query.OrderBy(a => a.Position).ToListAsync();
        return Ok(new { success = true, data = attrs.Select(a => FormatAttribute(a, includeOptions: true)).ToList() });
    }

    /// <summary>Get a single attribute with all its dropdown options</summary>
    /// <remarks>
    /// Returns one attribute and all its options (for select/multiselect types).
    /// Use this to load the choices for a dropdown field on the product form.
    /// </remarks>
    /// <param name="id">Attribute ID</param>
    [HttpGet("api/v1/admin/attributes/{id:int}")]
    public async Task<IActionResult> GetAttribute(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var a = await _db.Attributes
            .Include(a => a.Translations)
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);
        if (a == null) return NotFound(new { success = false, message = "Attribute not found." });
        return Ok(new { success = true, data = FormatAttribute(a, includeOptions: true) });
    }

    /// <summary>Create a new product attribute</summary>
    /// <remarks>
    /// Creates a new attribute that can be added to an attribute family and filled in when creating products.
    ///
    /// **Field guide:**
    /// - `code` — Unique machine key, lowercase underscore (e.g. `color`, `brand_name`). Cannot be changed after creation
    /// - `admin_name` — Label shown in the admin product form (e.g. "Color", "Brand Name")
    /// - `type` — Data type: `text` | `textarea` | `price` | `boolean` | `select` | `multiselect` | `date` | `image` | `file`
    /// - `label` — Customer-facing label shown in the app (English). Defaults to admin_name if not set
    /// - `is_required` — If true, product cannot be saved without filling this attribute. Default: false
    /// - `is_filterable` — If true, this attribute appears as a filter on the category page in the app. Default: false
    /// - `is_configurable` — If true, used to generate product variants (e.g. a "Size" attribute creates S/M/L variants). Default: false
    /// - `is_visible_on_front` — If true, shown on the product detail page. Default: true
    /// - `position` — Display order in the product form. Lower number = appears first
    ///
    /// **After creating a `select` or `multiselect` attribute, add its options** using `POST /api/v1/admin/attributes/{id}/options`
    ///
    /// **Example — create a Color dropdown:**
    /// ```json
    /// {
    ///   "code": "color",
    ///   "admin_name": "Color",
    ///   "type": "select",
    ///   "label": "Color",
    ///   "is_filterable": true,
    ///   "is_visible_on_front": true
    /// }
    /// ```
    /// </remarks>
    [HttpPost("api/v1/admin/attributes")]
    public async Task<IActionResult> CreateAttribute([FromBody] CreateAttributeRequest req)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new { success = false, message = "code is required." });
        if (string.IsNullOrWhiteSpace(req.AdminName))
            return BadRequest(new { success = false, message = "admin_name is required." });
        if (string.IsNullOrWhiteSpace(req.Type))
            return BadRequest(new { success = false, message = "type is required." });

        if (await _db.Attributes.AnyAsync(a => a.Code == req.Code))
            return Conflict(new { success = false, message = $"Attribute code '{req.Code}' already exists." });

        var now = DateTime.UtcNow;
        var attr = new CatalogAttribute
        {
            Code               = req.Code.Trim(),
            AdminName          = req.AdminName.Trim(),
            Type               = req.Type.Trim(),
            IsRequired         = req.IsRequired,
            IsFilterable       = req.IsFilterable,
            IsConfigurable     = req.IsConfigurable,
            IsVisibleOnFront   = req.IsVisibleOnFront,
            IsUserDefined      = true,
            Position           = req.Position,
            CreatedAt          = now,
            UpdatedAt          = now,
        };
        _db.Attributes.Add(attr);
        await _db.SaveChangesAsync();

        // Add English translation
        if (!string.IsNullOrWhiteSpace(req.Label))
        {
            _db.Set<AttributeTranslation>().Add(new AttributeTranslation
            {
                AttributeId = attr.Id,
                Locale      = "en",
                Name        = req.Label,
            });
            await _db.SaveChangesAsync();
        }

        return Ok(new { success = true, message = "Attribute created.", data = new { attr.Id, attr.Code, attr.AdminName, attr.Type } });
    }

    public record CreateAttributeRequest(
        string Code, string AdminName, string Type, string? Label = null,
        bool IsRequired = false, bool IsFilterable = false,
        bool IsConfigurable = false, bool IsVisibleOnFront = true,
        int? Position = null);

    // ═══════════════════════════════════════════════════════════════════════
    // ATTRIBUTE OPTIONS  (for select / multiselect attributes)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>List all options for a select/multiselect attribute</summary>
    /// <remarks>
    /// Returns the dropdown choices for a `select` or `multiselect` attribute.
    /// For example, the "Color" attribute's options might be: Red, Blue, Green, Black.
    ///
    /// **Use this to:** Load the choices for a dropdown on the product form.
    /// </remarks>
    /// <param name="id">Attribute ID</param>
    [HttpGet("api/v1/admin/attributes/{id:int}/options")]
    public async Task<IActionResult> ListOptions(int id)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var attr = await _db.Attributes
            .Include(a => a.Options).ThenInclude(o => o.Translations)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);
        if (attr == null) return NotFound(new { success = false, message = "Attribute not found." });
        return Ok(new { success = true, data = attr.Options.OrderBy(o => o.SortOrder).Select(FormatOption).ToList() });
    }

    /// <summary>Add a new option to a select/multiselect attribute</summary>
    /// <remarks>
    /// Adds a choice to a dropdown attribute. Only works on `select` or `multiselect` type attributes.
    ///
    /// **Field guide:**
    /// - `admin_name` — Label shown in the admin panel (required). E.g. "Red", "Extra Large"
    /// - `label` — Customer-facing label shown in the app. Defaults to admin_name if not provided
    /// - `sort_order` — Display order. 0 = first. Default: 0
    /// - `swatch_value` — For color attributes: the hex color code to show as a color swatch (e.g. `#FF0000` for red). Optional
    ///
    /// **Example — add "Red" to a Color attribute:**
    /// ```json
    /// {
    ///   "admin_name": "Red",
    ///   "label": "Red",
    ///   "sort_order": 1,
    ///   "swatch_value": "#FF0000"
    /// }
    /// ```
    /// </remarks>
    /// <param name="id">Attribute ID to add the option to</param>
    [HttpPost("api/v1/admin/attributes/{id:int}/options")]
    public async Task<IActionResult> AddOption(int id, [FromBody] AddOptionRequest req)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        if (!await _db.Attributes.AnyAsync(a => a.Id == id))
            return NotFound(new { success = false, message = "Attribute not found." });
        if (string.IsNullOrWhiteSpace(req.AdminName))
            return BadRequest(new { success = false, message = "admin_name is required." });

        var option = new AttributeOption
        {
            AttributeId = id,
            AdminName   = req.AdminName.Trim(),
            SortOrder   = req.SortOrder,
            SwatchValue = req.SwatchValue,
        };
        _db.AttributeOptions.Add(option);
        await _db.SaveChangesAsync();

        // English label
        var label = req.Label ?? req.AdminName;
        _db.Set<AttributeOptionTranslation>().Add(new AttributeOptionTranslation
        {
            AttributeOptionId = option.Id,
            Locale            = "en",
            Label             = label,
        });
        await _db.SaveChangesAsync();

        return Ok(new { success = true, message = "Option added.", data = new { option.Id, option.AdminName, label } });
    }

    public record AddOptionRequest(string AdminName, string? Label = null, int SortOrder = 0, string? SwatchValue = null);

    /// <summary>Delete a dropdown option from an attribute</summary>
    /// <remarks>
    /// Removes an option from a select/multiselect attribute.
    /// ⚠️ If products are already using this option value, their attribute value will be orphaned.
    /// </remarks>
    /// <param name="id">Attribute ID</param>
    /// <param name="optionId">Option ID to delete</param>
    [HttpDelete("api/v1/admin/attributes/{id:int}/options/{optionId:int}")]
    public async Task<IActionResult> DeleteOption(int id, int optionId)
    {
        if (!IsAdmin()) return AdminUnauthorized();
        var opt = await _db.AttributeOptions.FirstOrDefaultAsync(o => o.Id == optionId && o.AttributeId == id);
        if (opt == null) return NotFound(new { success = false, message = "Option not found." });
        _db.AttributeOptions.Remove(opt);
        await _db.SaveChangesAsync();
        return Ok(new { success = true, message = "Option deleted." });
    }

    // ─── Format helpers ───────────────────────────────────────────────────

    private static object FormatAttribute(CatalogAttribute a, bool includeOptions = false)
    {
        var label = a.Translations.FirstOrDefault(t => t.Locale == "en")?.Name
                    ?? a.Translations.FirstOrDefault()?.Name
                    ?? a.AdminName;
        return new
        {
            id               = a.Id,
            code             = a.Code,
            admin_name       = a.AdminName,
            label,
            type             = a.Type,
            is_required      = a.IsRequired,
            is_filterable    = a.IsFilterable,
            is_configurable  = a.IsConfigurable,
            is_visible       = a.IsVisibleOnFront,
            position         = a.Position,
            options          = includeOptions
                ? a.Options.OrderBy(o => o.SortOrder).Select(FormatOption).ToList()
                : null,
        };
    }

    private static object FormatOption(AttributeOption o)
    {
        var label = o.Translations.FirstOrDefault(t => t.Locale == "en")?.Label
                    ?? o.Translations.FirstOrDefault()?.Label
                    ?? o.AdminName;
        return new { id = o.Id, admin_name = o.AdminName, label, sort_order = o.SortOrder, swatch_value = o.SwatchValue };
    }
}
