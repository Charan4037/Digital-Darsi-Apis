using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

[Table("product_downloadable_links")]
public class ProductDownloadableLink
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("product_id")] public int ProductId { get; set; }
    [Column("url")] public string? Url { get; set; }
    [Column("file")] public string? File { get; set; }
    [Column("file_name")] public string? FileName { get; set; }
    [Column("type")] public string Type { get; set; } = "";
    [Column("price")] public decimal Price { get; set; }
    [Column("sample_url")] public string? SampleUrl { get; set; }
    [Column("sample_file")] public string? SampleFile { get; set; }
    [Column("sample_file_name")] public string? SampleFileName { get; set; }
    [Column("sample_type")] public string? SampleType { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("downloads")] public int Downloads { get; set; }
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }

    public Product? Product { get; set; }
}
