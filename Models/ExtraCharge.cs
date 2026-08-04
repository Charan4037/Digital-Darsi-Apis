using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models;

/// <summary>
/// An admin-defined additional charge (e.g. "Handling Charges", "Processing
/// Fee") applied to every active cart's total. See ExtraChargeService for
/// how ChargeType/Amount resolve to a rupee amount, and
/// AdminExtraChargesController for the CRUD surface.
/// </summary>
[Table("extra_charges")]
public class ExtraCharge
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";

    /// <summary>"fixed" (Amount is a flat rupee value) or "percentage"
    /// (Amount is applied against the cart subtotal, e.g. 2 = 2%).</summary>
    [Column("charge_type")] public string ChargeType { get; set; } = "fixed";
    [Column("amount")] public decimal Amount { get; set; }

    /// <summary>Null = applies to every cart (cart-wide). Set = only applies
    /// when the cart contains a product from this category or any of its
    /// subcategories — see ExtraChargeService.ComputeAsync.</summary>
    [Column("category_id")] public int? CategoryId { get; set; }

    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
