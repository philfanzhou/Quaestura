using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;
using Quaestura.Database.Entity;

namespace Quaestura.Domain.Services;

public interface ITagService
{
    Task<Tag?> GetAsync(Guid id);
    Task<(List<Tag> items, int totalPages, int totalCount)> ListAsync(int page, int size, string? name, string? sortBy);
    Task<(bool success, bool isDuplicate)> AddAsync(Tag tag, string userId);
    Task<(bool success, bool isDuplicate)> UpdateAsync(Tag tag, string userId);
    Task<(bool success, bool isReferenced)> DeleteAsync(Guid id);
}

public class TagService : ITagService
{
    private readonly QuaesturaDbContext _dbContext;

    public TagService(QuaesturaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Tag?> GetAsync(Guid id)
    {
        return await _dbContext.Tags.FindAsync(id);
    }

    public async Task<(List<Tag> items, int totalPages, int totalCount)> ListAsync(int page, int size, string? name, string? sortBy)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;
        if (size > 100) size = 100;

        var query = _dbContext.Tags.AsQueryable();

        if (!string.IsNullOrWhiteSpace(name))
        {
            var lowerName = name.ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(lowerName));
        }

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)size);

        IOrderedQueryable<Tag> ordered;
        if (string.Equals(sortBy, "usageCount", StringComparison.OrdinalIgnoreCase))
        {
            ordered = query.OrderByDescending(t => t.UsageCount).ThenBy(t => t.Name);
        }
        else
        {
            ordered = query.OrderBy(t => t.Name);
        }

        var items = await ordered
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items, totalPages, totalCount);
    }

    public async Task<(bool success, bool isDuplicate)> AddAsync(Tag tag, string userId)
    {
        var lowerName = tag.Name.Trim().ToLower();
        var exists = await _dbContext.Tags.AnyAsync(t => t.Name.ToLower() == lowerName);
        if (exists)
        {
            return (false, true);
        }

        tag.Id = Guid.NewGuid();
        tag.CreatedBy = userId;
        tag.CreatedAt = DateTimeOffset.UtcNow;
        tag.UsageCount = 0;

        await _dbContext.Tags.AddAsync(tag);
        await _dbContext.SaveChangesAsync();
        return (true, false);
    }

    public async Task<(bool success, bool isDuplicate)> UpdateAsync(Tag tag, string userId)
    {
        var existing = await _dbContext.Tags.FindAsync(tag.Id);
        if (existing == null) return (false, false);

        var lowerName = tag.Name.Trim().ToLower();
        var nameConflict = await _dbContext.Tags
            .AnyAsync(t => t.Id != tag.Id && t.Name.ToLower() == lowerName);
        if (nameConflict)
        {
            return (false, true);
        }

        existing.Name = tag.Name;
        existing.Color = tag.Color;
        existing.Description = tag.Description;

        await _dbContext.SaveChangesAsync();
        return (true, false);
    }

    public async Task<(bool success, bool isReferenced)> DeleteAsync(Guid id)
    {
        var tag = await _dbContext.Tags.FindAsync(id);
        if (tag == null) return (false, false);
        if (tag.UsageCount > 0) return (false, true);

        _dbContext.Tags.Remove(tag);
        await _dbContext.SaveChangesAsync();
        return (true, false);
    }
}
