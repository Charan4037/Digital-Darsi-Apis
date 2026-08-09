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
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
