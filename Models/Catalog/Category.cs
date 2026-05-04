using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BagistoApi.Models.Catalog;

[Table("categories")]
public class Category
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("position")]
    public int Position { get; set; }

    [Column("logo_path")]
    public string? LogoPath { get; set; }

    [Column("banner_path")]
    public string? BannerPath { get; set; }

    [Column("status")]
    public bool Status { get; set; }

    [Column("display_mode")]
    public string? DisplayMode { get; set; } = "products_and_description";

    [Column("_lft")]
    public int Lft { get; set; }

    [Column("_rgt")]
    public int Rgt { get; set; }

    [Column("parent_id")]
    public int? ParentId { get; set; }

    [Column("additional")]
    public string? Additional { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = new();
    public List<CategoryTranslation> Translations { get; set; } = new();
    public List<Product> Products { get; set; } = new();
    public List<Attribute> FilterableAttributes { get; set; } = new();
}

[Table("category_translations")]
public class CategoryTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("category_id")]
    public int CategoryId { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("slug")]
    public string Slug { get; set; } = "";

    [Column("url_path")]
    public string UrlPath { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("meta_title")]
    public string? MetaTitle { get; set; }

    [Column("meta_description")]
    public string? MetaDescription { get; set; }

    [Column("meta_keywords")]
    public string? MetaKeywords { get; set; }

    [Column("locale_id")]
    public int? LocaleId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "en";

    public Category? Category { get; set; }
}
