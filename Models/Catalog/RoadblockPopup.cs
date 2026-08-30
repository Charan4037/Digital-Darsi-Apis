using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

/// <summary>
/// A one-time promotional "roadblock" popup shown over the home screen when
/// the app opens, stored in the <c>dd_roadblock_popups</c> table. Global —
/// shown on the home screen regardless of which store section (foodstore/
/// buildstore/etc.) is selected, unlike <see cref="ScrapedBanner"/>. The
/// whole creative (heading, price, CTA button) is baked into
/// <see cref="ImageUrl"/> — there is no separate title/subtitle, since the
/// popup is a single tappable graphic plus a close button. Any number of
/// rows may be active at once — the app shows them one after another, each
/// dismissed before the next appears.
/// Served to the app by <c>RoadblockController</c>, managed by
/// <c>AdminRoadblockController</c>.
/// </summary>
[Table("dd_roadblock_popups")]
public class RoadblockPopup
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("image_url")]
    public string ImageUrl { get; set; } = "";

    /// <summary>What tapping this popup navigates to: none | url | category | product | vendor.</summary>
    [Column("link_type")]
    public string? LinkType { get; set; }

    /// <summary>Target URL when <see cref="LinkType"/> is "url".</summary>
    [Column("link_url")]
    public string? LinkUrl { get; set; }

    /// <summary>Target category when <see cref="LinkType"/> is "category".</summary>
    [Column("category_id")]
    public int? CategoryId { get; set; }

    /// <summary>Target product when <see cref="LinkType"/> is "product".</summary>
    [Column("product_id")]
    public int? ProductId { get; set; }

    /// <summary>Target vendor when <see cref="LinkType"/> is "vendor".</summary>
    [Column("vendor_id")]
    public int? VendorId { get; set; }

    /// <summary>Display order when more than one popup is active — lowest first.</summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>Admin on/off switch — inactive rows are kept (not deleted) but never served.</summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
