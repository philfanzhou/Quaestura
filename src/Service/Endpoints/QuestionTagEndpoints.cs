using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Quaestura.Domain.Services;
using Quaestura.Service.Middleware;
using Quaestura.Service.Models;

namespace Quaestura.Service.Endpoints;

public static class QuestionTagEndpoints
{
    public static WebApplication MapQuestionTagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/question-tags");

        group.MapPost("/batch-tag", BatchTag);
        group.MapGet("/", ListRelations);
        group.MapDelete("/", Remove);

        return app;
    }

    private static async Task<IResult> BatchTag(
        BatchTagQuestionRequest request,
        IQuestionTagService service,
        ITagService tagService,
        IValidator<BatchTagQuestionRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        // Validate all tag IDs exist up-front; reject with 404 if any missing
        var tagGuids = new List<Guid>();
        foreach (var tIdStr in request.TagIds)
        {
            if (!Guid.TryParse(tIdStr, out var tId))
                throw new ValidationException($"Tag id '{tIdStr}' is not a valid UUID");
            tagGuids.Add(tId);
        }

        foreach (var tId in tagGuids.Distinct())
        {
            var existing = await tagService.GetAsync(tId);
            if (existing == null)
                throw new EntityNotFoundException("Tag", tId.ToString());
        }

        int totalSuccess = 0;
        foreach (var qIdStr in request.QuestionIds)
        {
            if (!Guid.TryParse(qIdStr, out var qId))
                throw new ValidationException($"Question id '{qIdStr}' is not a valid UUID");
            var result = await service.TagAsync(qId, tagGuids);
            totalSuccess += result;
        }

        return Results.Ok(new { success = true, data = new { taggedCount = totalSuccess } });
    }

    private static async Task<IResult> ListRelations(
        IQuestionTagService service,
        IQuestionService questionService,
        IOssQuestionService ossService,
        [FromQuery] string? questionId,
        [FromQuery] string? tagId,
        [FromQuery] int page = 1,
        [FromQuery] int size = 10,
        [FromServices] ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(questionId) && string.IsNullOrWhiteSpace(tagId))
            throw new ValidationException("Either questionId or tagId is required");

        // 按 questionId 查询：返回关联 + Tag 信息
        if (!string.IsNullOrWhiteSpace(questionId))
        {
            if (!Guid.TryParse(questionId, out var qId))
                throw new ValidationException("questionId is not a valid UUID");

            var relations = await service.GetByQuestionAsync(qId);
            var dtos = relations.Select(r => new QuestionTagResponse
            {
                Id = r.Id.ToString(),
                QuestionId = r.QuestionId.ToString(),
                TagId = r.TagId.ToString(),
                TagName = r.Tag?.Name,
                TagColor = r.Tag?.Color,
                CreatedAt = r.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            }).ToList();

            return Results.Ok(new { success = true, data = dtos });
        }

        // 按 tagId 查询：返回题目列表（分页）
        if (!Guid.TryParse(tagId, out var tId))
            throw new ValidationException("tagId is not a valid UUID");

        var (questionIds, totalCount) = await service.GetQuestionIdsByTagAsync(tId, page, size);
        if (totalCount == 0)
        {
            return Results.Ok(new
            {
                success = true,
                data = new List<object>(),
                total = 0,
                page,
                size,
                totalPages = 0,
            });
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)size);
        var questions = await questionService.GetByIdsAsync(questionIds);
        var dtos2 = questions.Select(item => MapQuestionToResponse(item.Question, item.Content)).ToList();

        var logger = loggerFactory?.CreateLogger("QuestionTagEndpoints");
        foreach (var dto in dtos2)
        {
            if (dto.PicturePaths.Count == 0) continue;
            try
            {
                var urls = await ossService.GetPictureUrls(dto.PicturePaths.ToList());
                dto.PicturePaths.Clear();
                dto.PicturePaths.AddRange(urls);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to convert picture paths to URLs");
            }
        }

        return Results.Ok(new
        {
            success = true,
            data = dtos2,
            total = totalCount,
            page,
            size,
            totalPages,
        });
    }

    private static async Task<IResult> Remove(
        IQuestionTagService service,
        [FromQuery] string questionId,
        [FromQuery] string? tagId = null)
    {
        if (string.IsNullOrWhiteSpace(questionId))
            throw new ValidationException("questionId is required");
        if (!Guid.TryParse(questionId, out var qId))
            throw new ValidationException("questionId is not a valid UUID");

        int removed;
        if (!string.IsNullOrWhiteSpace(tagId))
        {
            if (!Guid.TryParse(tagId, out var tId))
                throw new ValidationException("tagId is not a valid UUID");
            removed = await service.UntagAsync(qId, new List<Guid> { tId });
            return Results.Ok(new { success = true, data = new { questionId, tagId, removed = true, count = removed } });
        }

        removed = await service.RemoveAllAsync(qId);
        return Results.Ok(new { success = true, data = new { questionId, removed = true, count = removed } });
    }

    private static QuestionResponse MapQuestionToResponse(Database.Entity.Question entity, Database.Entity.QuestionContent? content)
    {
        var dto = new QuestionResponse
        {
            Id = entity.Id.ToString(),
            Level = entity.Level,
            Type = entity.Type,
            Width = entity.Width,
            Height = entity.Height,
            UserId = entity.UserId,
            StudentId = entity.StudentId,
            MistakeId = entity.MistakeId,
            Subject = entity.Subject,
            Grade = entity.Grade,
            CreatedAt = entity.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            UpdatedAt = entity.UpdatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Content = content?.Content ?? string.Empty,
            CorrectAnswer = content?.CorrectAnswer ?? string.Empty,
            Analysis = content?.Analysis ?? string.Empty,
        };
        if (!string.IsNullOrEmpty(entity.PicturePaths))
        {
            var paths = JsonSerializer.Deserialize<List<string>>(entity.PicturePaths) ?? new List<string>();
            dto.PicturePaths.AddRange(paths);
        }
        return dto;
    }
}
