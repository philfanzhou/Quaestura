using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Ruoyu.Study.QuestionBank.Service.Middleware;
using Ruoyu.Study.QuestionBank.Service.Models;

namespace Ruoyu.Study.QuestionBank.Service.Endpoints;

public static class KnowledgeEndpoints
{
    public static WebApplication MapKnowledgeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/knowledges");

        group.MapGet("/", ListKnowledges);
        group.MapGet("/{id:guid}", GetKnowledge);
        group.MapPost("/", UpsertKnowledge);
        group.MapDelete("/{id:guid}", DeleteKnowledge);

        return app;
    }

    private static async Task<IResult> ListKnowledges(
        IKnowledgeService service,
        [FromQuery] string? parentId,
        [FromQuery] int? grade,
        [FromQuery] int? subject,
        [FromQuery] string? name,
        [FromQuery] int page = 1,
        [FromQuery] int size = 10)
    {
        // name 模糊搜索优先
        if (!string.IsNullOrWhiteSpace(name))
        {
            var matches = await service.GetLikeAsync(name);
            var dtos = matches.Select(MapToResponse).ToList();
            return Results.Ok(new
            {
                success = true,
                data = dtos,
                total = dtos.Count,
                page = 1,
                size = dtos.Count,
                totalPages = 1,
            });
        }

        // 按 (grade, subject) 分页
        if (grade.HasValue && grade.Value > 0 && subject.HasValue && subject.Value > 0)
        {
            var (items, totalPages, totalCount) = await service.GetByGradeAndSubjectAsync(
                grade.Value, subject.Value, page, size);
            return Results.Ok(new
            {
                success = true,
                data = items.Select(MapToResponse).ToList(),
                total = totalCount,
                page,
                size,
                totalPages,
            });
        }

        // 按 parentId 查子节点（不分页）
        if (parentId != null)
        {
            var children = await service.GetChildrenAsync(string.IsNullOrEmpty(parentId) ? null : parentId);
            return Results.Ok(new
            {
                success = true,
                data = children.Select(MapToResponse).ToList(),
                total = children.Count,
                page = 1,
                size = children.Count,
                totalPages = 1,
            });
        }

        // 全部分页
        var (allItems, allTotalPages, allTotalCount) = await service.GetAllAsync(page, size);
        return Results.Ok(new
        {
            success = true,
            data = allItems.Select(MapToResponse).ToList(),
            total = allTotalCount,
            page,
            size,
            totalPages = allTotalPages,
        });
    }

    private static async Task<IResult> GetKnowledge(Guid id, IKnowledgeService service)
    {
        var knowledge = await service.GetAsync(id.ToString());
        if (knowledge == null)
            throw new EntityNotFoundException("Knowledge", id.ToString());

        return Results.Ok(new { success = true, data = MapToResponse(knowledge) });
    }

    private static async Task<IResult> UpsertKnowledge(
        CreateKnowledgeRequest request,
        IKnowledgeService service,
        IValidator<CreateKnowledgeRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        var knowledge = new Knowledge
        {
            Id = string.IsNullOrWhiteSpace(request.Id) || !Guid.TryParse(request.Id, out var parsedId)
                ? Guid.NewGuid()
                : parsedId,
            Name = request.Name,
            Description = request.Description,
            ParentId = string.IsNullOrWhiteSpace(request.ParentId) ? null
                : Guid.TryParse(request.ParentId, out var parentId) ? parentId : null,
            Subject = request.Subject,
            Grade = request.Grade,
        };

        bool isCreate = string.IsNullOrWhiteSpace(request.Id);

        if (isCreate)
        {
            var (success, isDuplicate) = await service.AddAsync(knowledge, request.UserId);
            if (isDuplicate)
                throw new DomainException(
                    "Knowledge with same (subject, grade, name) already exists",
                    "QUESTIONBANK_KNOWLEDGE_DUPLICATE",
                    System.Net.HttpStatusCode.Conflict);
            if (!success)
                throw new DomainException(
                    "Create failed", "QUESTIONBANK_KNOWLEDGE_CREATE_FAILED",
                    System.Net.HttpStatusCode.InternalServerError);
        }
        else
        {
            var (success, isReferenced, wouldCreateCycle) = await service.UpdateAsync(knowledge, request.UserId);
            if (isReferenced)
                throw new BusinessPreconditionException(
                    "Knowledge is referenced and cannot be updated", "QUESTIONBANK_KNOWLEDGE_REFERENCED");
            if (wouldCreateCycle)
                throw new BusinessPreconditionException(
                    "Cannot update: would create circular parent reference", "QUESTIONBANK_KNOWLEDGE_CYCLE");
            if (!success)
                throw new EntityNotFoundException("Knowledge", request.Id!);
        }

        return Results.Ok(new { success = true, data = new { id = knowledge.Id.ToString() } });
    }

    private static async Task<IResult> DeleteKnowledge(Guid id, IKnowledgeService service)
    {
        var (success, isReferenced) = await service.DeleteAsync(id.ToString());
        if (isReferenced)
            throw new BusinessPreconditionException(
                "Knowledge is referenced and cannot be deleted", "QUESTIONBANK_KNOWLEDGE_REFERENCED");
        if (!success)
            throw new EntityNotFoundException("Knowledge", id.ToString());

        return Results.Ok(new { success = true, data = new { id = id.ToString(), deleted = true } });
    }

    private static KnowledgeResponse MapToResponse(Knowledge entity)
    {
        return new KnowledgeResponse
        {
            Id = entity.Id.ToString(),
            ParentId = entity.ParentId?.ToString(),
            Name = entity.Name,
            Description = entity.Description,
            CreatedBy = entity.CreatedBy,
            CreatedAt = entity.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            IsReferenced = entity.IsReferenced,
            Subject = entity.Subject ?? 0,
            Grade = entity.Grade ?? 0,
            UpdatedBy = entity.UpdatedBy,
            UpdatedAt = entity.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
    }
}
