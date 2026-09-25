using Microsoft.EntityFrameworkCore;
using Quaestura.Database.Entity;

namespace Quaestura.Database;

public class QuaesturaDbContext : DbContext
{
    public QuaesturaDbContext(DbContextOptions<QuaesturaDbContext> options) : base(options)
    {
    }

    public DbSet<Knowledge> Knowledges { get; set; } = null!;
    public DbSet<Question> Questions { get; set; } = null!;
    public DbSet<QuestionContent> QuestionContents { get; set; } = null!;
    public DbSet<QuestionKnowledge> QuestionKnowledges { get; set; } = null!;
    public DbSet<Tag> Tags { get; set; } = null!;
    public DbSet<QuestionTag> QuestionTags { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Knowledge 配置
        modelBuilder.Entity<Knowledge>(entity =>
        {
            entity.HasIndex(e => new { e.Subject, e.Grade, e.Name }).IsUnique();
            entity.HasIndex(e => new { e.Subject, e.Grade });
            entity.HasIndex(e => e.ParentId);

            entity.HasOne(e => e.Parent)
                  .WithMany(e => e.Children)
                  .HasForeignKey(e => e.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Question 配置
        modelBuilder.Entity<Question>(entity =>
        {
            entity.HasIndex(e => new { e.Subject, e.Grade });
            entity.HasIndex(e => new { e.Subject, e.Grade, e.Level });
            entity.HasIndex(e => new { e.Subject, e.Grade, e.Type });
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.UserId);

            entity.HasOne<QuestionContent>()
                  .WithOne(c => c.Question)
                  .HasForeignKey<QuestionContent>(c => c.QuestionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // QuestionContent 配置
        modelBuilder.Entity<QuestionContent>(entity =>
        {
            entity.HasIndex(e => e.QuestionId);
        });

        // QuestionKnowledge 配置
        modelBuilder.Entity<QuestionKnowledge>(entity =>
        {
            entity.HasIndex(e => new { e.QuestionId, e.KnowledgeId }).IsUnique();
            entity.HasIndex(e => e.KnowledgeId);

            entity.HasOne(e => e.Question)
                  .WithMany(q => q.QuestionKnowledges)
                  .HasForeignKey(e => e.QuestionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Knowledge)
                  .WithMany(k => k.QuestionKnowledges)
                  .HasForeignKey(e => e.KnowledgeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Tag 配置
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // QuestionTag 配置
        modelBuilder.Entity<QuestionTag>(entity =>
        {
            entity.HasIndex(e => new { e.QuestionId, e.TagId }).IsUnique();
            entity.HasIndex(e => e.TagId);

            entity.HasOne(e => e.Question)
                  .WithMany(q => q.QuestionTags)
                  .HasForeignKey(e => e.QuestionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tag)
                  .WithMany(t => t.QuestionTags)
                  .HasForeignKey(e => e.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
