using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.QuestionBank.Database.Entity;

[Table("question")]
public class Question
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset UpdatedAt { get; set; }

    [Column("level")]
    public int Level { get; set; }

    [Column("type")]
    public int Type { get; set; }

    [Column("width")]
    public int Width { get; set; }

    [Column("height")]
    public int Height { get; set; }

    [Column("picture_paths")]
    public string? PicturePaths { get; set; }

    [Column("user_id")]
    [MaxLength(36)]
    public string? UserId { get; set; }

    [Column("student_id")]
    [MaxLength(36)]
    public string? StudentId { get; set; }

    [Column("mistake_id")]
    [MaxLength(36)]
    public string? MistakeId { get; set; }

    [Column("subject")]
    public int Subject { get; set; }

    [Column("grade")]
    public int Grade { get; set; }

    [ForeignKey("Id")]
    public virtual ICollection<QuestionKnowledge> QuestionKnowledges { get; set; } = new List<QuestionKnowledge>();

    public virtual ICollection<QuestionTag> QuestionTags { get; set; } = new List<QuestionTag>();
}
