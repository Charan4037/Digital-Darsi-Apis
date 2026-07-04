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

    [Column("sort_order")]
    public int SortOrder { get; set; }
}
