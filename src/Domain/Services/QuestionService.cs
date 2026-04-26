using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Database.Oss;

namespace Ruoyu.Study.QuestionBank.Domain.Services;

public interface IQuestionService
{
    Task<(Question question, QuestionContent? content)> AddAsync(string userId, Question entity, QuestionContent? content, List<string> picturePaths, int subject, int grade);
    Task<(Question question, QuestionContent? content)?> GetAsync(Guid id);
    Task<bool> UpdateAsync(Question entity, QuestionContent? content, List<string>? picturePaths = null);
    Task<(List<QuestionWithContent> items, int totalPages, int totalCount)> SearchAsync(string? keyword, int? level, int? type, int subject, int grade, int page, int size);
    Task<List<QuestionWithContent>> GetByIdsAsync(List<Guid> ids);
    Task<bool> DeleteAsync(Guid id);
}

public class QuestionService : IQuestionService
{
    private readonly QuestionBankDbContext _dbContext;

    public QuestionService(QuestionBankDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<(Question question, QuestionContent? content)> AddAsync(string userId, Question entity, QuestionContent? content, List<string> picturePaths, int subject, int grade)
    {
        if (entity == null || (picturePaths.Count == 0 && string.IsNullOrEmpty(content?.Content)))
        {
            throw new ArgumentNullException(nameof(entity));
        }

        entity.Id = Guid.NewGuid();
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.UserId = userId;
        entity.Subject = subject;
        entity.Grade = grade;
        if (picturePaths.Count > 0)
        {
            entity.PicturePaths = JsonSerializer.Serialize(picturePaths);
        }

        _dbContext.Questions.Add(entity);

        if (content != null)
        {
            content.QuestionId = entity.Id;
            _dbContext.QuestionContents.Add(content);
        }

        await _dbContext.SaveChangesAsync();
        return (entity, content);
    }

    public async Task<(Question question, QuestionContent? content)?> GetAsync(Guid id)
    {
        var question = await _dbContext.Questions.FindAsync(id);
        if (question == null) return null;

        var content = await _dbContext.QuestionContents.FindAsync(id);
        return (question, content);
    }

    public async Task<bool> UpdateAsync(Question entity, QuestionContent? content, List<string>? picturePaths = null)
    {
        var existing = await _dbContext.Questions.FindAsync(entity.Id);
        if (existing == null) return false;

        existing.Level = entity.Level;
        existing.Type = entity.Type;
        existing.Grade = entity.Grade;
        existing.Subject = entity.Subject;
        existing.StudentId = entity.StudentId;
        existing.MistakeId = entity.MistakeId;
        existing.UpdatedAt = DateTime.UtcNow;
        if (picturePaths != null)
        {
            existing.PicturePaths = picturePaths.Count == 0 ? null : JsonSerializer.Serialize(picturePaths);
        }

        var existingContent = await _dbContext.QuestionContents.FindAsync(entity.Id);
        if (content != null)
        {
            if (existingContent == null)
            {
                content.QuestionId = entity.Id;
                _dbContext.QuestionContents.Add(content);
            }
            else
            {
                existingContent.Content = content.Content ?? existingContent.Content;
                existingContent.CorrectAnswer = content.CorrectAnswer ?? existingContent.CorrectAnswer;
                existingContent.Analysis = content.Analysis ?? existingContent.Analysis;
            }
        }

        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<(List<QuestionWithContent> items, int totalPages, int totalCount)> SearchAsync(
        string? keyword, int? level, int? type,
        int subject, int grade, int page, int size)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;
        if (size > 100) size = 100;

        var query = _dbContext.Questions
            .Include(q => q.QuestionKnowledges)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var contentQuery = _dbContext.QuestionContents.AsQueryable();
            var keywordLower = keyword.ToLower();
            var matchedContentQuestionIds = contentQuery
                .Where(c => c.Content != null && c.Content.ToLower().Contains(keywordLower))
                .Select(c => c.QuestionId);

            query = query.Where(q => matchedContentQuestionIds.Contains(q.Id));
        }

        if (level.HasValue && level.Value > 0)
        {
            query = query.Where(q => q.Level == level.Value);
        }

        if (type.HasValue && type.Value > 0)
        {
            query = query.Where(q => q.Type == type.Value);
        }

        query = query.Where(q => q.Subject == subject && q.Grade == grade);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)size);

        var questionIds = await query
            .OrderByDescending(q => q.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(q => q.Id)
            .ToListAsync();

        var questions = await _dbContext.Questions
            .Where(q => questionIds.Contains(q.Id))
            .ToListAsync();

        var contentQIds = questionIds;
        var contents = await _dbContext.QuestionContents
            .Where(c => contentQIds.Contains(c.QuestionId))
            .ToListAsync();

        var items = questions.Select(q => new QuestionWithContent
        {
            Question = q,
            Content = contents.FirstOrDefault(c => c.QuestionId == q.Id)
        }).ToList();

        return (items, totalPages, totalCount);
    }

    public async Task<List<QuestionWithContent>> GetByIdsAsync(List<Guid> ids)
    {
        var questions = await _dbContext.Questions
            .Where(q => ids.Contains(q.Id))
            .ToListAsync();

        var contents = await _dbContext.QuestionContents
            .Where(c => ids.Contains(c.QuestionId))
            .ToListAsync();

        return questions.Select(q => new QuestionWithContent
        {
            Question = q,
            Content = contents.FirstOrDefault(c => c.QuestionId == q.Id)
        }).ToList();
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var question = await _dbContext.Questions.FindAsync(id);
        if (question == null) return false;

        _dbContext.Questions.Remove(question);
        await _dbContext.SaveChangesAsync();
        return true;
    }
}

public class QuestionWithContent
{
    public Question Question { get; set; } = null!;
    public QuestionContent? Content { get; set; }

    public List<string> GetPicturePaths()
    {
        if (string.IsNullOrEmpty(Question.PicturePaths))
            return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(Question.PicturePaths) ?? new List<string>();
    }
}

/// <summary>
/// 题目图片上传服务
/// </summary>
public interface IOssQuestionService
{
    Task<string> UploadPictureAsync(Stream stream, string fileName, string contentType);
    Task<List<string>> UploadPicturesAsync(IEnumerable<(Stream stream, string fileName, string contentType)> pictures);
    Task<Stream> DownloadPictureAsync(string path);
    Task<List<Stream>> DownloadPicturesAsync(IEnumerable<string> paths);
    Task<bool> DeletePictureAsync(string path);
    Task<int> DeletePicturesAsync(IEnumerable<string> paths);
    string GetPictureUrl(string path);
    List<string> GetPictureUrls(IEnumerable<string> paths);
}

public class OssQuestionService : IOssQuestionService
{
    private readonly IOssService _ossService;

    public OssQuestionService(IOssService ossService)
    {
        _ossService = ossService;
    }

    public async Task<string> UploadPictureAsync(Stream stream, string fileName, string contentType)
    {
        return await _ossService.UploadAsync(stream, fileName, contentType, OssBucket.Questions);
    }

    public async Task<List<string>> UploadPicturesAsync(IEnumerable<(Stream stream, string fileName, string contentType)> pictures)
    {
        return await _ossService.UploadManyAsync(pictures, OssBucket.Questions);
    }

    public async Task<Stream> DownloadPictureAsync(string path)
    {
        return await _ossService.DownloadAsync(path);
    }

    public async Task<List<Stream>> DownloadPicturesAsync(IEnumerable<string> paths)
    {
        return await _ossService.DownloadManyAsync(paths);
    }

    public async Task<bool> DeletePictureAsync(string path)
    {
        return await _ossService.DeleteAsync(path);
    }

    public async Task<int> DeletePicturesAsync(IEnumerable<string> paths)
    {
        return await _ossService.DeleteManyAsync(paths);
    }

    public string GetPictureUrl(string path)
    {
        return _ossService.GetPresignedUrl(path);
    }

    public List<string> GetPictureUrls(IEnumerable<string> paths)
    {
        return _ossService.GetPresignedUrls(paths);
    }
}
