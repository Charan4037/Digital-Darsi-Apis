using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

[Table("attributes")]
public class Attribute
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("admin_name")]
    public string AdminName { get; set; } = "";

    [Column("type")]
    public string Type { get; set; } = "";

    [Column("swatch_type")]
    public string? SwatchType { get; set; }

    [Column("validation")]
    public string? Validation { get; set; }

    [Column("position")]
    public int? Position { get; set; }

    [Column("is_required")]
    public bool IsRequired { get; set; }

    [Column("is_unique")]
    public bool IsUnique { get; set; }

    [Column("is_filterable")]
    public bool IsFilterable { get; set; }

    [Column("is_comparable")]
    public bool IsComparable { get; set; }

    [Column("is_configurable")]
    public bool IsConfigurable { get; set; }

    [Column("is_user_defined")]
    public bool IsUserDefined { get; set; } = true;

    [Column("is_visible_on_front")]
    public bool IsVisibleOnFront { get; set; }

    [Column("value_per_locale")]
    public bool ValuePerLocale { get; set; }

    [Column("value_per_channel")]
    public bool ValuePerChannel { get; set; }

    [Column("enable_wysiwyg")]
    public bool EnableWysiwyg { get; set; }

    [Column("regex")]
    public string? Regex { get; set; }

    [Column("default_value")]
    public int? DefaultValue { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public List<AttributeTranslation> Translations { get; set; } = new();
    public List<AttributeOption> Options { get; set; } = new();
}

[Table("attribute_translations")]
public class AttributeTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("attribute_id")]
    public int AttributeId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("name")]
    public string? Name { get; set; }

    public Attribute? Attribute { get; set; }
}

[Table("attribute_families")]
public class AttributeFamily
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("status")]
    public bool Status { get; set; }

    [Column("is_user_defined")]
    public bool IsUserDefined { get; set; } = true;

    public List<AttributeGroup> Groups { get; set; } = new();
}

[Table("attribute_groups")]
public class AttributeGroup
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("attribute_family_id")]
    public int AttributeFamilyId { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("position")]
    public int Position { get; set; }

    [Column("is_user_defined")]
    public bool IsUserDefined { get; set; } = true;

    [Column("column")]
    public int ColumnName { get; set; } = 1;

    [Column("code")]
    public string? Code { get; set; }

    public AttributeFamily? AttributeFamily { get; set; }
    public List<Attribute> Attributes { get; set; } = new();
}

[Table("attribute_options")]
public class AttributeOption
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("attribute_id")]
    public int AttributeId { get; set; }

    [Column("admin_name")]
    public string? AdminName { get; set; }

    [Column("sort_order")]
    public int? SortOrder { get; set; }

    [Column("swatch_value")]
    public string? SwatchValue { get; set; }

    public Attribute? Attribute { get; set; }
    public List<AttributeOptionTranslation> Translations { get; set; } = new();
}

[Table("attribute_option_translations")]
public class AttributeOptionTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("attribute_option_id")]
    public int AttributeOptionId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("label")]
    public string? Label { get; set; }

    public AttributeOption? AttributeOption { get; set; }
}
