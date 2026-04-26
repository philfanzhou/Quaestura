using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.QuestionBank.Database.Entity;

[Table("question_content")]
public class QuestionContent
{
    [Key]
    [Column("question_id")]
    public Guid QuestionId { get; set; }

    [Column("content")]
    public string? Content { get; set; }

    [Column("correct_answer")]
    public string? CorrectAnswer { get; set; }

    [Column("analysis")]
    public string? Analysis { get; set; }

    [ForeignKey("QuestionId")]
    public virtual Question Question { get; set; } = null!;
}
