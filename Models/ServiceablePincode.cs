using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models;

[Table("serviceable_pincodes")]
public class ServiceablePincode
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("pincode")] public string Pincode { get; set; } = "";
    [Column("town")] public string Town { get; set; } = "";
    [Column("district")] public string? District { get; set; }
    [Column("postal_division")] public string? PostalDivision { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
