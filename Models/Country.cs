using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BagistoApi.Models;

[Table("countries")]
public class Country
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    public List<CountryTranslation> Translations { get; set; } = new();
    public List<CountryState> States { get; set; } = new();
}

[Table("country_translations")]
public class CountryTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("country_id")]
    public int CountryId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("name")]
    public string? Name { get; set; }

    public Country? Country { get; set; }
}

[Table("country_states")]
public class CountryState
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("country_id")]
    public int? CountryId { get; set; }

    [Column("country_code")]
    public string? CountryCode { get; set; }

    [Column("code")]
    public string? Code { get; set; }

    [Column("default_name")]
    public string? DefaultName { get; set; }

    public Country? Country { get; set; }
    public List<CountryStateTranslation> Translations { get; set; } = new();
}

[Table("country_state_translations")]
public class CountryStateTranslation
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("country_state_id")]
    public int CountryStateId { get; set; }

    [Column("locale")]
    public string Locale { get; set; } = "";

    [Column("default_name")]
    public string? DefaultName { get; set; }

    public CountryState? CountryState { get; set; }
}
