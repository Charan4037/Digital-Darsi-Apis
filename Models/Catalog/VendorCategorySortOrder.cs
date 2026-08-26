using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

/// <summary>
/// A per-category override of a vendor's home-page priority — lets a vendor
/// rank first in one category (e.g. "Vegetables") while a different vendor
/// ranks first in another (e.g. "Dairy"), instead of Vendor.SortOrder's flat
/// value applying the same rank everywhere. See
/// CategoryController.QueryCategoryProductsAsync for how this resolves
/// against the storefront: when a row exists for a vendor in the category
/// being browsed, it replaces (not adds to) that vendor's global SortOrder
/// for that listing only. Same bulk-add/GroupId convention as
/// DeliveryTypeCategoryPrice/ExtraCharge.
/// </summary>
[Table("vendor_category_sort_orders")]
public class VendorCategorySortOrder
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("vendor_id")] public int VendorId { get; set; }
    [Column("category_id")] public int CategoryId { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }

    /// <summary>Null for a normal single add. Set to a shared GUID for every
    /// row created together via the "scope to several categories at once"
    /// bulk-add — lets the admin UI group them back into one card instead
    /// of showing N nearly-identical rows for what's really one priority
    /// applied to many categories. Purely a display grouping key — each row
    /// is still fully independent (editable/deletable on its own) once
    /// created.</summary>
    [Column("group_id")] public string? GroupId { get; set; }

    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
