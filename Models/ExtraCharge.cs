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

    /// <summary>Null for a normal single add. Set to a shared GUID for every
    /// row created together via the "scope to several categories at once"
    /// bulk-add — lets the admin UI group them back into one card instead
    /// of showing N nearly-identical rows for what's really one charge
    /// definition applied to many categories. Purely a display grouping key
    /// — each row is still fully independent (its own active state, can be
    /// edited/deleted on its own) once created.</summary>
    [Column("group_id")] public string? GroupId { get; set; }

    /// <summary>Null = no lower bound. Set = this charge only applies when
    /// the cart subtotal is greater than or equal to this amount — e.g. a
    /// "Small Order Fee" that only kicks in for carts under some value would
    /// instead use <see cref="MaxCartValue"/>, while a surcharge that only
    /// applies to large orders (say, above ₹1000) uses this. See
    /// ExtraChargeService.ComputeAsync.</summary>
    [Column("min_cart_value")] public decimal? MinCartValue { get; set; }

    /// <summary>Null = no upper bound. Set = this charge only applies when
    /// the cart subtotal is less than or equal to this amount — e.g. a
    /// "Small Order Fee" that only applies below ₹1000. Can be combined with
    /// <see cref="MinCartValue"/> to scope a charge to a value range.</summary>
    [Column("max_cart_value")] public decimal? MaxCartValue { get; set; }

    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
