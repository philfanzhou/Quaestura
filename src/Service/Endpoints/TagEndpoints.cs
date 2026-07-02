using System;
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

public static class TagEndpoints
{
    public static WebApplication MapTagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/tags");

        group.MapGet("/", ListTags);
        group.MapGet("/{id:guid}", GetTag);
        group.MapPost("/", UpsertTag);
        group.MapDelete("/{id:guid}", DeleteTag);

        return app;
    }

    private static async Task<IResult> ListTags(
        ITagService service,
        [FromQuery] string? name,
        [FromQuery] string? sortBy,
        [FromQuery] int page = 1,
        [FromQuery] int size = 10)
    {
        var (items, totalPages, totalCount) = await service.ListAsync(
            page, size, name, sortBy);

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

    private static async Task<IResult> GetTag(Guid id, ITagService service)
    {
        var tag = await service.GetAsync(id);
        if (tag == null)
            throw new EntityNotFoundException("Tag", id.ToString());

        return Results.Ok(new { success = true, data = MapToResponse(tag) });
    }

    private static async Task<IResult> UpsertTag(
        CreateTagRequest request,
        ITagService service,
        IValidator<CreateTagRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        var tag = new Tag
        {
            Id = string.IsNullOrWhiteSpace(request.Id) || !Guid.TryParse(request.Id, out var parsedId)
                ? Guid.NewGuid()
                : parsedId,
            Name = request.Name.Trim(),
            Color = request.Color,
            Description = request.Description,
        };

        bool isCreate = string.IsNullOrWhiteSpace(request.Id);

        if (isCreate)
        {
            var (success, isDuplicate) = await service.AddAsync(tag, request.UserId);
            if (isDuplicate)
                throw new DomainException(
                    "Tag with same name already exists",
                    "QUESTIONBANK_TAG_DUPLICATE",
                    System.Net.HttpStatusCode.Conflict);
            if (!success)
                throw new DomainException(
                    "Create failed", "QUESTIONBANK_TAG_CREATE_FAILED",
                    System.Net.HttpStatusCode.InternalServerError);
        }
        else
        {
            var (success, isDuplicate) = await service.UpdateAsync(tag, request.UserId);
            if (isDuplicate)
                throw new DomainException(
                    "Tag with same name already exists",
                    "QUESTIONBANK_TAG_DUPLICATE",
                    System.Net.HttpStatusCode.Conflict);
            if (!success)
                throw new EntityNotFoundException("Tag", request.Id!);
        }

        return Results.Ok(new { success = true, data = new { id = tag.Id.ToString() } });
    }

    private static async Task<IResult> DeleteTag(Guid id, ITagService service)
    {
        var (success, isReferenced) = await service.DeleteAsync(id);
        if (isReferenced)
            throw new BusinessPreconditionException(
                "Tag is referenced by questions and cannot be deleted",
                "QUESTIONBANK_TAG_REFERENCED");
        if (!success)
            throw new EntityNotFoundException("Tag", id.ToString());

        return Results.Ok(new { success = true, data = new { id = id.ToString(), deleted = true } });
    }

    private static TagResponse MapToResponse(Tag entity)
    {
        return new TagResponse
        {
            Id = entity.Id.ToString(),
            Name = entity.Name,
            Color = entity.Color,
            Description = entity.Description,
            CreatedBy = entity.CreatedBy,
            CreatedAt = entity.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            UsageCount = entity.UsageCount,
        };
    }
}
