using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.QuestionBank.Database.Entity;

[Table("question_knowledge")]
public class QuestionKnowledge
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("question_id")]
    [Required]
    public Guid QuestionId { get; set; }

    [Column("knowledge_id")]
    [Required]
    public Guid KnowledgeId { get; set; }

    [Column("weight")]
    public double Weight { get; set; } = 1.0;

    [ForeignKey("QuestionId")]
    public virtual Question Question { get; set; } = null!;

    [ForeignKey("KnowledgeId")]
    public virtual Knowledge Knowledge { get; set; } = null!;
}
