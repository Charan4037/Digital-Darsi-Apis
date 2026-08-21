using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DOSApi.Models.Catalog;

/// <summary>
/// A delivery time-of-day window (e.g. "9 AM - 12 PM") offered by one
/// specific PreorderRule. Owned by the rule — deleted along with it — not
/// shared across rules, since each rule can define its own set.
/// </summary>
[Table("preorder_slots")]
public class PreorderSlot
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("preorder_rule_id")] public int PreorderRuleId { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("start_time")] public TimeSpan StartTime { get; set; }
    [Column("end_time")] public TimeSpan EndTime { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime? CreatedAt { get; set; }
    [Column("updated_at")] public DateTime? UpdatedAt { get; set; }
}
