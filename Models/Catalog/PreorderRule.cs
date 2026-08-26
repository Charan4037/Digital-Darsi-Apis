using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

/// <summary>
/// Marks a product/vendor/category/location (or the whole catalog) as under
/// preorder. When more than one rule could apply to the same product, the
/// most specific scope wins — see PreorderService.ResolveAsync for the
/// precedence walk (Product > Vendor > Category > Location > Global).
/// Each rule owns its own delivery time slots (PreorderSlot) rather than
/// picking from one shared list, so different rules can offer different
/// windows.
/// </summary>
[Table("preorder_rules")]
public class PreorderRule
{
    [Key, Column("id")] public int Id { get; set; }

    /// <summary>One of "global" | "location" | "category" | "vendor" | "product".</summary>
    [Column("scope_type")] public string ScopeType { get; set; } = "";

    [Column("product_id")] public int? ProductId { get; set; }
    [Column("vendor_id")] public int? VendorId { get; set; }
    [Column("category_id")] public int? CategoryId { get; set; }

    /// <summary>Shared GUID across several product- or category-scoped rows
    /// created together as one multi-select rule — same pattern as
    /// DeliveryTypeCategoryPrice.GroupId. Null for a single-target rule and
    /// for every vendor/location/global rule.</summary>
    [Column("group_id")] public string? GroupId { get; set; }

    /// <summary>Matches ServiceablePincode.Pincode for "location" scope.</summary>
    [Column("pincode")] public string? Pincode { get; set; }

    [Column("is_active")] public bool IsActive { get; set; } = true;

    /// <summary>How many days ahead a customer can schedule delivery,
    /// starting from today. Null falls back to PreorderService.DefaultWindowDays.</summary>
    [Column("window_days")] public int? WindowDays { get; set; }

    /// <summary>Minimum notice, in hours, required before a slot's start
    /// time — e.g. 12 means a slot can only be picked if it starts at least
    /// 12 hours from now. This is what makes same-day delivery reachable
    /// (an evening slot booked in the morning) while still respecting prep
    /// time. Null falls back to PreorderService.DefaultMinLeadHours.</summary>
    [Column("min_lead_hours")] public int? MinLeadHours { get; set; }

    /// <summary>Optional customer-facing note, e.g. "Restocking, ships next week".</summary>
    [Column("note")] public string? Note { get; set; }

    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
