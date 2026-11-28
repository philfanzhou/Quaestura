using System.Collections.Generic;
using System.Text.Json;
using Quaestura.Database.Entity;

namespace Quaestura.Service.Models;

/// <summary>
/// HTTP response DTOs for QuestionBank endpoints.
/// All field names use camelCase to align with HTTP/JSON conventions.
/// Timestamps are ISO 8601 UTC strings.
/// </summary>
public class QuestionResponse
{
    public string Id { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Type { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<string> PicturePaths { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public string CorrectAnswer { get; set; } = string.Empty;
    public string Analysis { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? StudentId { get; set; }
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    internal static QuestionResponse From(Question entity, QuestionContent? content)
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

public class KnowledgeResponse
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public bool IsReferenced { get; set; }
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdatedAt { get; set; }
}

public class QuestionKnowledgeResponse
{
    public string Id { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public string KnowledgeId { get; set; } = string.Empty;
    public double Weight { get; set; }
    public int Subject { get; set; }
    public int Grade { get; set; }
}

public class TagResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public int UsageCount { get; set; }
}

public class QuestionTagResponse
{
    public string Id { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;
    public string? TagName { get; set; }
    public string? TagColor { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class AdminAuthCallbackResponse
{
    public List<string> Roles { get; set; } = new();
}
