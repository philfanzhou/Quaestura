using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Ruoyu.Study.QuestionBank.Service.Middleware;
using Ruoyu.Study.QuestionBank.Service.Models;
using Ruoyu.Study.QuestionBank.Service.Validation;

namespace Ruoyu.Study.QuestionBank.Service.Endpoints;

public static class QuestionEndpoints
{
    public static WebApplication MapQuestionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/questions");

        group.MapGet("/", SearchQuestions);
        group.MapGet("/{id:guid}", GetQuestion);
        group.MapPost("/", UploadQuestion).DisableAntiforgery();
        group.MapDelete("/{id:guid}", DeleteQuestion);

        return app;
    }

    private static async Task<IResult> SearchQuestions(
        IQuestionService service,
        IOssQuestionService ossService,
        IQuestionTagService questionTagService,
        [FromQuery] string? keyword,
        [FromQuery] int? level,
        [FromQuery] int? type,
        [FromQuery] int? subject,
        [FromQuery] int? grade,
        [FromQuery] string? tagId,
        [FromQuery] int page = 1,
        [FromQuery] int size = 10,
        [FromServices] ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger("QuestionEndpoints");

        if (!subject.HasValue || subject.Value <= 0)
            throw new ValidationException("Subject is required and must be greater than 0");
        if (!grade.HasValue || grade.Value <= 0)
            throw new ValidationException("Grade is required and must be greater than 0");

        HashSet<Guid>? tagQuestionSet = null;
        if (!string.IsNullOrWhiteSpace(tagId))
        {
            if (!Guid.TryParse(tagId, out var tId))
                throw new ValidationException("tagId is not a valid UUID");
            // Fetch all (no paging) question IDs for the tag to filter in-memory.
            // For small data volumes this is acceptable; revisit if tag usage grows.
            const int largeSize = 10000;
            var (taggedIds, _) = await questionTagService.GetQuestionIdsByTagAsync(tId, 1, largeSize);
            tagQuestionSet = new HashSet<Guid>(taggedIds);
        }

        var (items, totalPages, totalCount) = await service.SearchAsync(
            string.IsNullOrWhiteSpace(keyword) ? null : keyword,
            level.HasValue && level.Value > 0 ? level : null,
            type.HasValue && type.Value > 0 ? type : null,
            subject.Value, grade.Value, page, size);

        if (tagQuestionSet != null)
        {
            items = items.Where(i => tagQuestionSet.Contains(i.Question.Id)).ToList();
            totalCount = items.Count;
            totalPages = (int)Math.Ceiling(totalCount / (double)size);
            // Re-apply paging since the filtered set may be smaller than the original page
            items = items.Skip((page - 1) * size).Take(size).ToList();
        }

        var dtos = items.Select(item => MapToResponse(item.Question, item.Content)).ToList();
        await ConvertPicturePathsToUrlsAsync(ossService, dtos, logger);

        return Results.Ok(new
        {
            success = true,
            data = dtos,
            total = totalCount,
            page,
            size,
            totalPages,
        });
    }

    private static async Task<IResult> GetQuestion(
        Guid id,
        IQuestionService service,
        IOssQuestionService ossService)
    {
        var result = await service.GetAsync(id);
        if (result == null)
            throw new EntityNotFoundException("Question", id.ToString());

        var dto = MapToResponse(result.Value.question, result.Value.content);
        await ConvertPicturePathsToUrlsAsync(ossService, dto);

        return Results.Ok(new { success = true, data = dto });
    }

    private static async Task<IResult> UploadQuestion(
        HttpRequest request,
        IQuestionService service,
        IOssQuestionService ossService,
        IValidator<CreateQuestionRequest> validator,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("QuestionEndpoints");

        if (!request.HasFormContentType)
            throw new ValidationException("Request must be multipart/form-data");

        var form = await request.ReadFormAsync();

        if (!form.TryGetValue("question", out var questionJson) || string.IsNullOrWhiteSpace(questionJson))
            throw new ValidationException("question is required");

        if (!form.TryGetValue("userId", out var userIdValues) || string.IsNullOrWhiteSpace(userIdValues))
            throw new ValidationException("userId is required");

        if (!form.TryGetValue("subject", out var subjectValues) || !int.TryParse(subjectValues, out var subject) || subject <= 0)
            throw new ValidationException("subject is required and must be greater than 0");

        if (!form.TryGetValue("grade", out var gradeValues) || !int.TryParse(gradeValues, out var grade) || grade <= 0)
            throw new ValidationException("grade is required and must be greater than 0");

        var questionPayload = JsonSerializer.Deserialize<CreateQuestionRequest>(
            questionJson.ToString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ValidationException("Invalid question JSON");

        var validation = await validator.ValidateAsync(questionPayload);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        QuestionContentPayload? contentPayload = null;
        if (form.TryGetValue("content", out var contentJson) && !string.IsNullOrWhiteSpace(contentJson))
        {
            contentPayload = JsonSerializer.Deserialize<QuestionContentPayload>(
                contentJson.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        // Validate and read uploaded pictures
        var pictureFiles = form.Files.GetFiles("pictures");
        var pictureStreams = new List<(Stream stream, string fileName, string contentType)>();
        var pictureBytesList = new List<byte[]>();

        try
        {
            foreach (var file in pictureFiles)
            {
                var bytes = ImageValidationHelper.ReadAndValidate(file);
                pictureBytesList.Add(bytes);

                var stream = new MemoryStream(bytes);
                var ext = Path.GetExtension(file.FileName);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                var safeFileName = $"q_{Guid.NewGuid():N}{ext}";
                pictureStreams.Add((stream, safeFileName, file.ContentType));
            }

            // Upload to OSS
            var uploadedPaths = pictureStreams.Count > 0
                ? await ossService.UploadPicturesAsync(pictureStreams)
                : new List<string>();

            // Update or create
            string? resultId;
            if (!string.IsNullOrWhiteSpace(questionPayload.Id)
                && Guid.TryParse(questionPayload.Id, out var existingId))
            {
                var existing = await service.GetAsync(existingId);
                if (existing == null)
                    throw new EntityNotFoundException("Question", questionPayload.Id);

                var entity = existing.Value.question;
                entity.Level = questionPayload.Level;
                entity.Type = questionPayload.Type;
                entity.Width = questionPayload.Width;
                entity.Height = questionPayload.Height;
                entity.Grade = grade;
                entity.Subject = subject;
                entity.StudentId = questionPayload.StudentId;
                entity.MistakeId = questionPayload.MistakeId;
                entity.UpdatedAt = DateTimeOffset.UtcNow;

                var content = contentPayload == null
                    ? null
                    : new QuestionContent
                    {
                        Content = contentPayload.Content,
                        CorrectAnswer = contentPayload.CorrectAnswer,
                        Analysis = contentPayload.Analysis,
                    };

                await service.UpdateAsync(entity, content,
                    uploadedPaths.Count > 0 ? uploadedPaths : null);
                resultId = entity.Id.ToString();
            }
            else
            {
                var entity = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Level = questionPayload.Level,
                    Type = questionPayload.Type,
                    Width = questionPayload.Width,
                    Height = questionPayload.Height,
                    UserId = userIdValues.ToString(),
                    StudentId = questionPayload.StudentId,
                    MistakeId = questionPayload.MistakeId,
                    Subject = subject,
                    Grade = grade,
                };

                var content = contentPayload == null
                    ? null
                    : new QuestionContent
                    {
                        Content = contentPayload.Content,
                        CorrectAnswer = contentPayload.CorrectAnswer,
                        Analysis = contentPayload.Analysis,
                    };

                var (created, _) = await service.AddAsync(
                    userIdValues.ToString(), entity, content, uploadedPaths, subject, grade);
                resultId = created.Id.ToString();
            }

            logger.LogInformation("Question uploaded: {QuestionId}", resultId);

            return Results.Ok(new { success = true, data = new { id = resultId } });
        }
        finally
        {
            foreach (var (stream, _, _) in pictureStreams)
            {
                stream.Dispose();
            }
        }
    }

    private static async Task<IResult> DeleteQuestion(
        Guid id,
        IQuestionService service,
        IOssQuestionService ossService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("QuestionEndpoints");

        var existing = await service.GetAsync(id);
        if (existing == null)
            throw new EntityNotFoundException("Question", id.ToString());

        var picturePaths = string.IsNullOrEmpty(existing.Value.question.PicturePaths)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(existing.Value.question.PicturePaths) ?? new List<string>();

        var deleted = await service.DeleteAsync(id);
        if (!deleted)
            throw new DomainException("Delete failed", "QUESTIONBANK_QUESTION_DELETE_FAILED", System.Net.HttpStatusCode.InternalServerError);

        // Await OSS deletion so failures are surfaced
        if (picturePaths.Count > 0)
        {
            try
            {
                await ossService.DeletePicturesAsync(picturePaths);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete pictures from OSS for question {QuestionId}", id);
            }
        }

        logger.LogInformation("Question deleted: {QuestionId}", id);

        return Results.Ok(new { success = true, data = new { id = id.ToString(), deleted = true } });
    }

    private static QuestionResponse MapToResponse(Question entity, QuestionContent? content)
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

    private static async Task ConvertPicturePathsToUrlsAsync(
        IOssQuestionService ossService,
        QuestionResponse dto,
        ILogger? logger = null)
    {
        if (dto.PicturePaths.Count == 0) return;

        try
        {
            var urls = await ossService.GetPictureUrls(dto.PicturePaths.ToList());
            dto.PicturePaths.Clear();
            dto.PicturePaths.AddRange(urls);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to generate presigned URLs for question {QuestionId}", dto.Id);
        }
    }

    private static async Task ConvertPicturePathsToUrlsAsync(
        IOssQuestionService ossService,
        List<QuestionResponse> dtos,
        ILogger? logger = null)
    {
        foreach (var dto in dtos)
        {
            await ConvertPicturePathsToUrlsAsync(ossService, dto, logger);
        }
    }
}
