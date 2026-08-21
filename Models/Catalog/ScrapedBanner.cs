using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

/// <summary>
/// A homepage promotional banner scraped from a storefront's slider into the
/// <c>dd_scraped_banners</c> staging table (populated by the scraper's
/// <c>scrape_banners.py</c>). Served to the app by <c>BannersController</c>.
/// </summary>
[Table("dd_scraped_banners")]
public class ScrapedBanner
{
    [Key, Column("id")]
    public int Id { get; set; }

    /// <summary>Source site: buildstore / foodstore / store / services.</summary>
    [Column("site_key")]
    public string SiteKey { get; set; } = "";

    [Column("image_url")]
    public string ImageUrl { get; set; } = "";

    [Column("title")]
    public string? Title { get; set; }

    [Column("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>Where the slide links to on the source site, if anywhere.</summary>
    [Column("link_url")]
    public string? LinkUrl { get; set; }

    /// <summary>What tapping this banner navigates to: none | url | category | product.
    /// Null/empty is treated as legacy data — derived from <see cref="LinkUrl"/> instead
    /// (see BannersController) for banners saved before this field existed.</summary>
    [Column("link_type")]
    public string? LinkType { get; set; }

    /// <summary>Target category when <see cref="LinkType"/> is "category".</summary>
    [Column("category_id")]
    public int? CategoryId { get; set; }

    /// <summary>Target product when <see cref="LinkType"/> is "product".</summary>
    [Column("product_id")]
    public int? ProductId { get; set; }

    /// <summary>Target vendor when <see cref="LinkType"/> is "vendor".</summary>
    [Column("vendor_id")]
    public int? VendorId { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }
}
