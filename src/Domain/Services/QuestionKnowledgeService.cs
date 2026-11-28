using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;
using Quaestura.Database.Entity;

namespace Quaestura.Domain.Services;

public interface IQuestionKnowledgeService
{
    Task<int> TagAsync(Guid questionId, IList<(Guid knowledgeId, double weight)> tags, int subject, int grade);
    Task<bool> RemoveAllTagsAsync(Guid questionId);
    Task<List<QuestionKnowledge>> GetByQuestionAsync(Guid questionId);
    Task<List<Guid>> GetQuestionsByTagAsync(Guid knowledgeId);
    Task<(List<Guid> questionIds, int totalCount)> GetQuestionsByTagAsync(Guid knowledgeId, int page, int size);
}

public class QuestionKnowledgeService : IQuestionKnowledgeService
{
    private readonly QuaesturaDbContext _dbContext;

    public QuestionKnowledgeService(QuaesturaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<int> TagAsync(Guid questionId, IList<(Guid knowledgeId, double weight)> tags, int subject, int grade)
    {
        if (tags == null || tags.Count == 0)
            return Task.FromResult(0);

        var ids = tags.Select(t => t.knowledgeId).Distinct().ToArray();
        return KnowledgeReferences.ChangeAsync(_dbContext, questionId, ids,
            () => AddBindingsAsync(questionId, tags, ids, subject, grade));
    }

    private async Task<int> AddBindingsAsync(Guid questionId, IList<(Guid knowledgeId, double weight)> tags,
        Guid[] ids, int subject, int grade)
    {
        var questionMatches = await _dbContext.Questions.AnyAsync(q =>
            q.Id == questionId && q.Subject == subject && q.Grade == grade);
        if (!questionMatches) return 0;

        var tagEntities = await _dbContext.Knowledges
            .Where(k => ids.Contains(k.Id))
            .ToListAsync();
        var tagDict = tagEntities.ToDictionary(k => k.Id);

        var validTags = tags.Where(t =>
                tagDict.TryGetValue(t.knowledgeId, out var k) &&
                k.Grade == grade &&
                k.Subject == subject)
            .ToList();

        if (validTags.Count == 0) return 0;

        var existing = await _dbContext.QuestionKnowledges
            .Where(qk => qk.QuestionId == questionId)
            .Select(qk => qk.KnowledgeId)
            .ToListAsync();

        var existingSet = new HashSet<Guid>(existing);

        var toInsert = validTags
            .Where(t => !existingSet.Contains(t.knowledgeId))
            .Select(t => new QuestionKnowledge
            {
                Id = Guid.NewGuid(),
                QuestionId = questionId,
                KnowledgeId = t.knowledgeId,
                Weight = t.weight
            })
            .ToList();

        if (toInsert.Count == 0) return 0;

        foreach (var tag in toInsert)
        {
            if (tagDict.TryGetValue(tag.KnowledgeId, out var knowledge))
            {
                KnowledgeReferences.SetReferenced(_dbContext, knowledge, true);
            }
        }

        _dbContext.QuestionKnowledges.AddRange(toInsert);
        await _dbContext.SaveChangesAsync();
        return toInsert.Count;
    }

    public Task<bool> RemoveAllTagsAsync(Guid questionId) =>
        KnowledgeReferences.ChangeAsync(_dbContext, questionId, null, async () =>
        {
            await KnowledgeReferences.RemoveBindingsAsync(_dbContext, questionId);
            await _dbContext.SaveChangesAsync();
            return true;
        });

    public async Task<List<QuestionKnowledge>> GetByQuestionAsync(Guid questionId)
    {
        return await _dbContext.QuestionKnowledges
            .Where(qk => qk.QuestionId == questionId)
            .Include(qk => qk.Question)
            .ToListAsync();
    }

    public async Task<List<Guid>> GetQuestionsByTagAsync(Guid knowledgeId)
    {
        return await _dbContext.QuestionKnowledges
            .Where(qk => qk.KnowledgeId == knowledgeId)
            .Select(qk => qk.QuestionId)
            .Distinct()
            .ToListAsync();
    }

    public async Task<(List<Guid> questionIds, int totalCount)> GetQuestionsByTagAsync(Guid knowledgeId, int page, int size)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;

        var query = _dbContext.QuestionKnowledges
            .Where(qk => qk.KnowledgeId == knowledgeId)
            .Select(qk => qk.QuestionId)
            .Distinct();

        var totalCount = await query.CountAsync();

        var questionIds = await query
            .OrderBy(q => q)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (questionIds, totalCount);
    }
}
