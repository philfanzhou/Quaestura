using System;
using System.Collections.Generic;
using System.Linq;
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

public static class QuestionKnowledgeEndpoints
{
    public static WebApplication MapQuestionKnowledgeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/question-knowledges");

        group.MapPost("/batch-tag", BatchTag);
        group.MapGet("/", ListRelations);
        group.MapDelete("/", RemoveAll);

        return app;
    }

    private static async Task<IResult> BatchTag(
        BatchTagKnowledgeRequest request,
        IQuestionKnowledgeService service,
        IValidator<BatchTagKnowledgeRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        if (!Guid.TryParse(request.KnowledgeId, out var knowledgeId))
            throw new ValidationException("KnowledgeId is not a valid UUID");

        var tags = new List<(Guid knowledgeId, double weight)> { (knowledgeId, 1.0) };

        int totalSuccess = 0;
        foreach (var qIdStr in request.QuestionIds)
        {
            if (!Guid.TryParse(qIdStr, out var qId))
            {
                throw new ValidationException($"Question id '{qIdStr}' is not a valid UUID");
            }
            var result = await service.TagAsync(qId, tags, request.Subject, request.Grade);
            totalSuccess += result;
        }

        return Results.Ok(new { success = true, data = new { taggedCount = totalSuccess } });
    }

    private static async Task<IResult> ListRelations(
        IQuestionKnowledgeService service,
        IQuestionService questionService,
        IOssQuestionService ossService,
        [FromQuery] string? questionId,
        [FromQuery] string? knowledgeId,
        [FromQuery] int page = 1,
        [FromQuery] int size = 10,
        [FromServices] ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(questionId) && string.IsNullOrWhiteSpace(knowledgeId))
            throw new ValidationException("Either questionId or knowledgeId is required");

        // Query by questionId: return the association list
        if (!string.IsNullOrWhiteSpace(questionId))
        {
            if (!Guid.TryParse(questionId, out var qId))
                throw new ValidationException("questionId is not a valid UUID");

            var relations = await service.GetByQuestionAsync(qId);
            var dtos = relations.Select(r => new QuestionKnowledgeResponse
            {
                Id = r.Id.ToString(),
                QuestionId = r.QuestionId.ToString(),
                KnowledgeId = r.KnowledgeId.ToString(),
                Weight = r.Weight,
                Subject = r.Question.Subject,
                Grade = r.Question.Grade,
            }).ToList();

            return Results.Ok(new { success = true, data = dtos });
        }

        // Query by knowledgeId: return the question list (paginated)
        if (!Guid.TryParse(knowledgeId, out var kId))
            throw new ValidationException("knowledgeId is not a valid UUID");

        var (questionIds, totalCount) = await service.GetQuestionsByTagAsync(kId, page, size);
        return await RelatedQuestionPage.CreateAsync(questionIds, totalCount, page, size,
            questionService, ossService, loggerFactory, nameof(QuestionKnowledgeEndpoints));
    }

    private static async Task<IResult> RemoveAll(
        IQuestionKnowledgeService service,
        [FromQuery] string questionId)
    {
        if (string.IsNullOrWhiteSpace(questionId))
            throw new ValidationException("questionId is required");
        if (!Guid.TryParse(questionId, out var qId))
            throw new ValidationException("questionId is not a valid UUID");

        var success = await service.RemoveAllTagsAsync(qId);
        if (!success)
            throw new DomainException(
                "Failed to remove all question knowledges",
                "QUAESTURA_QUESTION_KNOWLEDGE_REMOVE_FAILED",
                System.Net.HttpStatusCode.InternalServerError);

        return Results.Ok(new { success = true, data = new { questionId, removed = true } });
    }
}
