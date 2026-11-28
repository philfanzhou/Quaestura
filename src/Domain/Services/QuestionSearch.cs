using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;

namespace Quaestura.Domain.Services;

public sealed class QuestionSearch(QuaesturaDbContext db)
{
    public async Task<(List<QuestionWithContent> items, int totalPages, int totalCount)> SearchAsync(
        string? keyword, int? level, int? type, int subject, int grade, int page, int size, Guid? tagId = null)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 10;
        if (size > 100) size = 100;

        var questions = db.Questions.Where(q => q.Subject == subject && q.Grade == grade);
        if (tagId.HasValue)
            questions = questions.Where(q => q.QuestionTags.Any(t => t.TagId == tagId.Value));
        if (level is > 0)
            questions = questions.Where(q => q.Level == level.Value);
        if (type is > 0)
            questions = questions.Where(q => q.Type == type.Value);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.ToLower();
            questions = questions.Where(q => db.QuestionContents.Any(c =>
                c.QuestionId == q.Id && c.Content != null && c.Content.ToLower().Contains(term)));
        }

        var totalCount = await questions.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)size);
        var offset = (long)(page - 1) * size;
        if (offset >= totalCount) return ([], totalPages, totalCount);

        var results =
            from question in questions
            join content in db.QuestionContents on question.Id equals content.QuestionId into contents
            from content in contents.DefaultIfEmpty()
            orderby question.CreatedAt descending, question.Id
            select new QuestionWithContent { Question = question, Content = content };
        var items = await results.Skip((int)offset).Take(size).ToListAsync();
        return (items, totalPages, totalCount);
    }
}
