using System.Collections.Generic;

namespace Quaestura.Service.Models;

/// <summary>
/// HTTP request DTOs for QuestionBank endpoints.
/// All field names use camelCase to align with HTTP/JSON conventions.
/// UserId is never read from the request body; it is always resolved from the
/// authenticated JWT (sub claim) in the endpoint layer.
/// </summary>
public class CreateQuestionRequest
{
    public string? Id { get; set; }
    public int Level { get; set; }
    public int Type { get; set; }
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public string? StudentId { get; set; }
    public string? MistakeId { get; set; }
}

public class QuestionContentPayload
{
    public string? Content { get; set; }
    public string? CorrectAnswer { get; set; }
    public string? Analysis { get; set; }
}

public class CreateKnowledgeRequest
{
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? Subject { get; set; }
    public int? Grade { get; set; }
}

public class BatchTagKnowledgeRequest
{
    public List<string> QuestionIds { get; set; } = new();
    public string KnowledgeId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
}

public class CreateTagRequest
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Description { get; set; }
}

public class BatchTagQuestionRequest
{
    public List<string> QuestionIds { get; set; } = new();
    public List<string> TagIds { get; set; } = new();
}
