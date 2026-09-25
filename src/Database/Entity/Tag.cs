using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Quaestura.Database.Entity;

[Table("tag")]
public class Tag
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("color")]
    [MaxLength(20)]
    public string? Color { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("created_by")]
    [MaxLength(36)]
    public string? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("usage_count")]
    public int UsageCount { get; set; }

    public virtual ICollection<QuestionTag> QuestionTags { get; set; } = new List<QuestionTag>();
}
