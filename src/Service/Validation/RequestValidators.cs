using FluentValidation;
using Ruoyu.Study.QuestionBank.Service.Models;

namespace Ruoyu.Study.QuestionBank.Service.Validation;

public class CreateQuestionRequestValidator : AbstractValidator<CreateQuestionRequest>
{
    public CreateQuestionRequestValidator()
    {
        RuleFor(x => x.Level)
            .GreaterThanOrEqualTo(0).WithMessage("Level must not be negative");

        RuleFor(x => x.Type)
            .GreaterThanOrEqualTo(0).WithMessage("Type must not be negative");
    }
}

public class CreateKnowledgeRequestValidator : AbstractValidator<CreateKnowledgeRequest>
{
    public CreateKnowledgeRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required");

        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("UserId is required");

        When(x => x.Subject.HasValue, () =>
        {
            RuleFor(x => x.Subject!.Value)
                .GreaterThan(0).WithMessage("Subject must be greater than 0 when provided");
        });

        When(x => x.Grade.HasValue, () =>
        {
            RuleFor(x => x.Grade!.Value)
                .GreaterThan(0).WithMessage("Grade must be greater than 0 when provided");
        });
    }
}

public class BatchTagKnowledgeRequestValidator : AbstractValidator<BatchTagKnowledgeRequest>
{
    public BatchTagKnowledgeRequestValidator()
    {
        RuleFor(x => x.QuestionIds)
            .NotEmpty().WithMessage("At least one question id is required");

        RuleFor(x => x.KnowledgeId)
            .NotEmpty().WithMessage("KnowledgeId is required");

        RuleFor(x => x.Subject)
            .GreaterThan(0).WithMessage("Subject must be greater than 0");

        RuleFor(x => x.Grade)
            .GreaterThan(0).WithMessage("Grade must be greater than 0");

        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("UserId is required");
    }
}

public class CreateTagRequestValidator : AbstractValidator<CreateTagRequest>
{
    public CreateTagRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters");

        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("UserId is required");

        When(x => !string.IsNullOrEmpty(x.Color), () =>
        {
            RuleFor(x => x.Color!)
                .Matches("^#[0-9A-Fa-f]{6}$")
                .WithMessage("Color must be a valid HEX color (e.g., #FF6B6B)");
        });
    }
}

public class BatchTagQuestionRequestValidator : AbstractValidator<BatchTagQuestionRequest>
{
    public BatchTagQuestionRequestValidator()
    {
        RuleFor(x => x.QuestionIds)
            .NotEmpty().WithMessage("At least one question id is required");

        RuleFor(x => x.TagIds)
            .NotEmpty().WithMessage("At least one tag id is required");

        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("UserId is required");
    }
}
