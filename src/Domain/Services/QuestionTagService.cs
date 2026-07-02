using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database;
using Ruoyu.Study.QuestionBank.Database.Entity;

namespace Ruoyu.Study.QuestionBank.Domain.Services;

public interface IQuestionTagService
{
    /// <summary>
    /// Tag a single question with multiple tags. Existing relations are skipped silently.
    /// Returns the number of newly created relations and updates the tag usage_count.
    /// </summary>
    Task<int> TagAsync(Guid questionId, IList<Guid> tagIds);

    /// <summary>
    /// Remove specific tag relations for a question and decrement usage_count.
    /// </summary>
    Task<int> UntagAsync(Guid questionId, IList<Guid> tagIds);

    /// <summary>
    /// Remove all tag relations for a question and decrement usage_count for each affected tag.
    /// </summary>
    Task<int> RemoveAllAsync(Guid questionId);

    /// <summary>
    /// List all relations for a question (with Tag navigation data).
    /// </summary>
    Task<List<QuestionTag>> GetByQuestionAsync(Guid questionId);

    /// <summary>
    /// Paged list of question IDs that have a given tag.
    /// </summary>
    Task<(List<Guid> questionIds, int totalCount)> GetQuestionIdsByTagAsync(Guid tagId, int page, int size);
}

public class QuestionTagService : IQuestionTagService
{
    private readonly QuestionBankDbContext _dbContext;

    public QuestionTagService(QuestionBankDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> TagAsync(Guid questionId, IList<Guid> tagIds)
    {
        if (tagIds == null || tagIds.Count == 0) return 0;

        var questionExists = await _dbContext.Questions.AnyAsync(q => q.Id == questionId);
        if (!questionExists) return 0;

        var distinctTagIds = tagIds.Distinct().ToList();
        var existingRelations = await _dbContext.QuestionTags
            .Where(qt => qt.QuestionId == questionId && distinctTagIds.Contains(qt.TagId))
            .Select(qt => qt.TagId)
            .ToListAsync();
        var existingSet = new HashSet<Guid>(existingRelations);

        var newTagIds = distinctTagIds.Where(id => !existingSet.Contains(id)).ToList();
        if (newTagIds.Count == 0) return 0;

        // Verify tags exist
        var existingTags = await _dbContext.Tags
            .Where(t => newTagIds.Contains(t.Id))
            .ToListAsync();
        var validTagIds = existingTags.Select(t => t.Id).ToHashSet();

        var toInsert = newTagIds
            .Where(id => validTagIds.Contains(id))
            .Select(id => new QuestionTag
            {
                Id = Guid.NewGuid(),
                QuestionId = questionId,
                TagId = id,
                CreatedAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        if (toInsert.Count == 0) return 0;

        await _dbContext.QuestionTags.AddRangeAsync(toInsert);

        // Increment usage_count for each newly tagged tag
        foreach (var tag in existingTags.Where(t => newTagIds.Contains(t.Id)))
        {
            tag.UsageCount += 1;
        }

        await _dbContext.SaveChangesAsync();
        return toInsert.Count;
    }

    public async Task<int> UntagAsync(Guid questionId, IList<Guid> tagIds)
    {
        if (tagIds == null || tagIds.Count == 0) return 0;

        var relations = await _dbContext.QuestionTags
            .Where(qt => qt.QuestionId == questionId && tagIds.Contains(qt.TagId))
            .ToListAsync();

        if (relations.Count == 0) return 0;

        var affectedTagIds = relations.Select(r => r.TagId).Distinct().ToList();
        _dbContext.QuestionTags.RemoveRange(relations);

        var affectedTags = await _dbContext.Tags
            .Where(t => affectedTagIds.Contains(t.Id))
            .ToListAsync();
        foreach (var tag in affectedTags)
        {
            if (tag.UsageCount > 0) tag.UsageCount -= 1;
        }

        await _dbContext.SaveChangesAsync();
        return relations.Count;
    }

    public async Task<int> RemoveAllAsync(Guid questionId)
    {
        var relations = await _dbContext.QuestionTags
            .Where(qt => qt.QuestionId == questionId)
            .ToListAsync();

        if (relations.Count == 0) return 0;

        var affectedTagIds = relations.Select(r => r.TagId).Distinct().ToList();
        _dbContext.QuestionTags.RemoveRange(relations);

        var affectedTags = await _dbContext.Tags
            .Where(t => affectedTagIds.Contains(t.Id))
            .ToListAsync();
        foreach (var tag in affectedTags)
        {
            if (tag.UsageCount > 0) tag.UsageCount -= 1;
        }

        await _dbContext.SaveChangesAsync();
        return relations.Count;
    }

    public async Task<List<QuestionTag>> GetByQuestionAsync(Guid questionId)
    {
        return await _dbContext.QuestionTags
            .Include(qt => qt.Tag)
            .Where(qt => qt.QuestionId == questionId)
            .ToListAsync();
    }

    public async Task<(List<Guid> questionIds, int totalCount)> GetQuestionIdsByTagAsync(Guid tagId, int page, int size)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;

        var query = _dbContext.QuestionTags
            .Where(qt => qt.TagId == tagId)
            .Select(qt => qt.QuestionId);

        var totalCount = await query.CountAsync();
        var questionIds = await query
            .Distinct()
            .OrderBy(id => id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (questionIds, totalCount);
    }
}
