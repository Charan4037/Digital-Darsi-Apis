using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BagistoApi.Models;

[Table("delivery_types")]
public class DeliveryType
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("code")] public string Code { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("description")] public string? Description { get; set; }
    [Column("price")] public decimal Price { get; set; }
    [Column("delivery_hours")] public int DeliveryHours { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
