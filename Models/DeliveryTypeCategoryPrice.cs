using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models;

/// <summary>
/// A per-category price override for a DeliveryType tier (e.g. "Express"
/// costs more for the "Fragile" category than its global default). See
/// DeliveryChargeService for how these resolve against a cart — when a cart
/// spans categories with different applicable rates for the same tier, the
/// highest one wins.
/// </summary>
[Table("delivery_type_category_prices")]
public class DeliveryTypeCategoryPrice
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("delivery_type_id")] public int DeliveryTypeId { get; set; }
    [Column("category_id")] public int CategoryId { get; set; }
    [Column("price")] public decimal Price { get; set; }

    /// <summary>Null for a normal single add. Set to a shared GUID for every
    /// row created together via the "scope to several categories at once"
    /// bulk-add — lets the admin UI group them back into one card instead
    /// of showing N nearly-identical rows for what's really one price
    /// applied to many categories. Purely a display grouping key — each row
    /// is still fully independent (its own active state, editable/deletable
    /// on its own) once created.</summary>
    [Column("group_id")] public string? GroupId { get; set; }

    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
