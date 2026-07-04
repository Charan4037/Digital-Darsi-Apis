using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Cms;

[Table("cms_pages")]
public class CmsPage
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("layout")]
    public string? Layout { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public List<CmsPageTranslation> Translations { get; set; } = new();
}

[Table("cms_page_translations")]
public class CmsPageTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("cms_page_id")]
    public int CmsPageId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("page_title")]
    public string? PageTitle { get; set; }

    [Column("url_key")]
    public string? UrlKey { get; set; }

    [Column("meta_title")]
    public string? MetaTitle { get; set; }

    [Column("meta_keywords")]
    public string? MetaKeywords { get; set; }

    [Column("meta_description")]
    public string? MetaDescription { get; set; }

    [Column("html_content")]
    public string? HtmlContent { get; set; }

    public CmsPage? CmsPage { get; set; }
}
