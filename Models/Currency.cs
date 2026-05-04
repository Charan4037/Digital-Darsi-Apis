using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BagistoApi.Models;

[Table("currencies")]
public class Currency
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("symbol")]
    public string? Symbol { get; set; }

    [Column("decimal")]
    public int Decimal { get; set; } = 2;

    [Column("group_separator")]
    public string GroupSeparator { get; set; } = ",";

    [Column("decimal_separator")]
    public string DecimalSeparator { get; set; } = ".";

    [Column("currency_position")]
    public string? CurrencyPosition { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
