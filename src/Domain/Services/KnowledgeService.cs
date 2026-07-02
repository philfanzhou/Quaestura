using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database;
using Ruoyu.Study.QuestionBank.Database.Entity;

namespace Ruoyu.Study.QuestionBank.Domain.Services;

public interface IKnowledgeService
{
    Task<(List<Knowledge> items, int totalPages, int totalCount)> GetAllAsync(int page, int size);
    Task<List<Knowledge>> GetChildrenAsync(string? parentId);
    Task<(bool success, bool isDuplicate)> AddAsync(Knowledge knowledge, string userId);
    Task<(bool success, bool isReferenced, bool wouldCreateCycle)> UpdateAsync(Knowledge knowledge, string userId);
    Task<(bool success, bool isReferenced)> DeleteAsync(string id);
    Task<List<Knowledge>> GetLikeAsync(string name);
    Task<Knowledge?> GetAsync(string id);
    Task<(List<Knowledge> items, int totalPages, int totalCount)> GetByGradeAndSubjectAsync(int grade, int subject, int page, int size);
}

public class KnowledgeService : IKnowledgeService
{
    private readonly QuestionBankDbContext _dbContext;

    public KnowledgeService(QuestionBankDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<(List<Knowledge> items, int totalPages, int totalCount)> GetAllAsync(int page, int size)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;

        var query = _dbContext.Knowledges.AsQueryable();
        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)size);

        var items = await query
            .OrderBy(k => k.Name)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items, totalPages, totalCount);
    }

    public async Task<List<Knowledge>> GetChildrenAsync(string? parentId)
    {
        Guid? pid = string.IsNullOrEmpty(parentId) ? (Guid?)null : Guid.Parse(parentId);

        var query = _dbContext.Knowledges.AsQueryable();
        if (pid.HasValue)
        {
            query = query.Where(k => k.ParentId == pid.Value);
        }
        else
        {
            query = query.Where(k => k.ParentId == null);
        }

        return await query.OrderBy(k => k.Name).ToListAsync();
    }

    public async Task<(bool success, bool isDuplicate)> AddAsync(Knowledge knowledge, string userId)
    {
        if (await _dbContext.Knowledges.AnyAsync(k => k.Name == knowledge.Name))
        {
            return (false, true);
        }

        knowledge.CreatedBy = userId;
        knowledge.CreatedAt = DateTimeOffset.UtcNow;
        await _dbContext.Knowledges.AddAsync(knowledge);
        await _dbContext.SaveChangesAsync();
        return (true, false);
    }

    public async Task<(bool success, bool isReferenced, bool wouldCreateCycle)> UpdateAsync(Knowledge knowledge, string userId)
    {
        var existing = await _dbContext.Knowledges.FindAsync(knowledge.Id);
        if (existing == null) return (false, false, false);

        if (existing.IsReferenced) return (false, true, false);
        if (knowledge.ParentId.HasValue)
        {
            var parent = await _dbContext.Knowledges.FindAsync(knowledge.ParentId.Value);
            if (parent != null)
            {
                if (WouldCreateCycle(knowledge.Id, knowledge.ParentId.Value, parent))
                {
                    return (false, false, true);
                }
            }
        }

        existing.Name = knowledge.Name;
        existing.ParentId = knowledge.ParentId;
        existing.Description = knowledge.Description;
        existing.Subject = knowledge.Subject;
        existing.Grade = knowledge.Grade;
        existing.UpdatedBy = userId;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync();
        return (true, false, false);
    }

    public async Task<(bool success, bool isReferenced)> DeleteAsync(string id)
    {
        var knowledge = await _dbContext.Knowledges.FindAsync(Guid.Parse(id));
        if (knowledge == null) return (false, false);
        if (knowledge.IsReferenced) return (false, true);

        _dbContext.Knowledges.Remove(knowledge);
        await _dbContext.SaveChangesAsync();
        return (true, false);
    }

    public async Task<List<Knowledge>> GetLikeAsync(string name)
    {
        return await _dbContext.Knowledges
            .Where(k => k.Name != null && k.Name.Contains(name))
            .OrderBy(k => k.Name)
            .ToListAsync();
    }

    public async Task<Knowledge?> GetAsync(string id)
    {
        return await _dbContext.Knowledges.FindAsync(Guid.Parse(id));
    }

    public async Task<(List<Knowledge> items, int totalPages, int totalCount)> GetByGradeAndSubjectAsync(int grade, int subject, int page, int size)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;

        var query = _dbContext.Knowledges
            .Where(k => k.Grade == grade && k.Subject == subject);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)size);

        var items = await query
            .OrderBy(k => k.Name)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items, totalPages, totalCount);
    }

    private static bool WouldCreateCycle(Guid targetId, Guid parentId, Knowledge parent)
    {
        if (parentId == targetId) return true;

        var visited = new HashSet<Guid>();
        var current = parent;

        while (current.ParentId.HasValue)
        {
            if (!visited.Add(current.Id)) break;
            if (current.ParentId.Value == targetId) return true;

            current = current.Parent;
            if (current == null) break;
        }

        return false;
    }
}
