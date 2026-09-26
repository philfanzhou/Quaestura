using System.Collections.Generic;

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
    public string? MistakeId { get; set; }
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
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
