using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;
using Quaestura.Database.Entity;

namespace Quaestura.Domain.Services;

internal static class KnowledgeReferences
{
    public static async Task<T> ChangeAsync<T>(QuaesturaDbContext db, Guid questionId,
        Guid[]? knowledgeIds, Func<Task<T>> change)
    {
        if (!db.Database.IsNpgsql()) return await change();

        await using var transaction = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync()
            : null;

        // Stabilize this question's bindings first. Different questions can share points;
        // lock those points in the same order before checking for remaining references.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM question WHERE id = {questionId} FOR UPDATE");
        knowledgeIds ??= await db.QuestionKnowledges.Where(r => r.QuestionId == questionId)
            .Select(r => r.KnowledgeId).ToArrayAsync();
        if (knowledgeIds.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM knowledge WHERE id = ANY({knowledgeIds}) ORDER BY id FOR UPDATE");

        var result = await change();
        if (transaction != null) await transaction.CommitAsync();
        return result;
    }

    public static async Task RemoveBindingsAsync(QuaesturaDbContext db, Guid questionId)
    {
        var relations = await db.QuestionKnowledges.Where(r => r.QuestionId == questionId).ToListAsync();
        if (relations.Count == 0) return;

        var ids = relations.Select(r => r.KnowledgeId).ToArray();
        var unused = await db.Knowledges.Where(k => ids.Contains(k.Id)
            && !k.QuestionKnowledges.Any(r => r.QuestionId != questionId)).ToListAsync();
        foreach (var knowledge in unused) SetReferenced(db, knowledge, false);
        db.QuestionKnowledges.RemoveRange(relations);
    }

    public static void SetReferenced(QuaesturaDbContext db, Knowledge knowledge, bool value)
    {
        knowledge.IsReferenced = value;
        // A batch can reuse a tracked point across transactions. Persist the value even if
        // it matches the tracked original but another request changed it in the meantime.
        db.Entry(knowledge).Property(k => k.IsReferenced).IsModified = true;
    }
}
