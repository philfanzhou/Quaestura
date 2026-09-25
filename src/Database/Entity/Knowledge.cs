using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Quaestura.Database.Entity;

[Table("knowledge")]
public class Knowledge
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parent_id")]
    public Guid? ParentId { get; set; }

    [Column("name")]
    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("created_by")]
    [MaxLength(36)]
    public string? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("is_referenced")]
    public bool IsReferenced { get; set; }

    [Column("subject")]
    public int? Subject { get; set; }

    [Column("grade")]
    public int? Grade { get; set; }

    [Column("updated_by")]
    [MaxLength(36)]
    public string? UpdatedBy { get; set; }

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset? UpdatedAt { get; set; }

    [ForeignKey("ParentId")]
    public virtual Knowledge? Parent { get; set; }

    public virtual ICollection<Knowledge> Children { get; set; } = new List<Knowledge>();

    public virtual ICollection<QuestionKnowledge> QuestionKnowledges { get; set; } = new List<QuestionKnowledge>();
}
