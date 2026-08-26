using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

/// <summary>
/// One product a vendor-scoped PreorderRule optionally narrows to — see
/// PreorderService.ResolveAsync's vendor-scope branch. A rule with zero rows
/// here still matches its whole vendor, unchanged from before this table
/// existed. Rows are replaced wholesale on rule edit (PreorderScopeFilterSeeder),
/// not independently toggled — no IsActive/UpdatedAt.
/// </summary>
[Table("preorder_rule_products")]
public class PreorderRuleProduct
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("preorder_rule_id")] public int PreorderRuleId { get; set; }
    [Column("product_id")] public int ProductId { get; set; }
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
}
