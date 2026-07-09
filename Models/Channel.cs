using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models;

[Table("channels")]
public class Channel
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("timezone")]
    public string? Timezone { get; set; }

    [Column("theme")]
    public string? Theme { get; set; }

    [Column("hostname")]
    public string? Hostname { get; set; }

    [Column("logo")]
    public string? Logo { get; set; }

    [Column("favicon")]
    public string? Favicon { get; set; }

    [Column("home_seo")]
    public string? HomeSeo { get; set; }

    [Column("is_maintenance_on")]
    public bool IsMaintenanceOn { get; set; }

    [Column("allowed_ips")]
    public string? AllowedIps { get; set; }

    [Column("root_category_id")]
    public int? RootCategoryId { get; set; }

    [Column("default_locale_id")]
    public int DefaultLocaleId { get; set; }

    [Column("base_currency_id")]
    public int BaseCurrencyId { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Locale? DefaultLocale { get; set; }
    public Currency? BaseCurrency { get; set; }
    public List<ChannelTranslation> Translations { get; set; } = new();
    public List<Locale> Locales { get; set; } = new();
    public List<Currency> Currencies { get; set; } = new();
}

[Table("channel_translations")]
public class ChannelTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("channel_id")]
    public int ChannelId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("maintenance_mode_text")]
    public string? MaintenanceModeText { get; set; }

    [Column("home_seo")]
    public string? HomeSeo { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public Channel? Channel { get; set; }
}
