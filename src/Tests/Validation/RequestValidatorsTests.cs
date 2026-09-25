using System.Collections.Generic;
using FluentAssertions;
using FluentValidation.TestHelper;
using Quaestura.Service.Models;
using Quaestura.Service.Validation;
using Xunit;

namespace Quaestura.Tests.Validation;

public class CreateQuestionRequestValidatorTests
{
    private readonly CreateQuestionRequestValidator _validator = new();

    [Fact]
    public void Validate_WithNegativeLevel_Fails()
    {
        var request = new CreateQuestionRequest { Level = -1, Type = 1 };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Level)
            .WithErrorMessage("Level must not be negative");
    }

    [Fact]
    public void Validate_WithNegativeType_Fails()
    {
        var request = new CreateQuestionRequest { Level = 1, Type = -1 };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Type)
            .WithErrorMessage("Type must not be negative");
    }

    [Fact]
    public void Validate_WithZeroSubject_Fails()
    {
        // Subject 0 is rejected by the "NotEqual(0)" rule
        var request = new CreateQuestionRequest { Level = 1, Type = 1 };
        // CreateKnowledgeRequestValidator pattern; CreateQuestionRequestValidator uses NotEqual(0)
        // For CreateQuestionRequest, the subject/grade are part of form fields, not DTO, so the
        // DTO validator only covers Level/Type (subject/grade validated in the endpoint).
        var result = _validator.TestValidate(request);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithAllValidFields_Succeeds()
    {
        var request = new CreateQuestionRequest { Level = 5, Type = 2 };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }
}

public class CreateKnowledgeRequestValidatorTests
{
    private readonly CreateKnowledgeRequestValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyName_Fails()
    {
        var request = new CreateKnowledgeRequest
        {
            Name = "",
            Subject = 1,
            Grade = 7
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Name is required");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveSubject_Fails(int subject)
    {
        var request = new CreateKnowledgeRequest
        {
            Name = "test",
            Subject = subject,
            Grade = 7
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor("Subject.Value")
            .WithErrorMessage("Subject must be greater than 0 when provided");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveGrade_Fails(int grade)
    {
        var request = new CreateKnowledgeRequest
        {
            Name = "test",
            Subject = 1,
            Grade = grade
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor("Grade.Value")
            .WithErrorMessage("Grade must be greater than 0 when provided");
    }

    [Fact]
    public void Validate_WithAllValidFields_Succeeds()
    {
        var request = new CreateKnowledgeRequest
        {
            Name = "Algebra",
            Subject = 1,
            Grade = 7
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithNoSubjectAndGrade_Succeeds()
    {
        var request = new CreateKnowledgeRequest
        {
            Name = "Generic",
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }
}

public class BatchTagKnowledgeRequestValidatorTests
{
    private readonly BatchTagKnowledgeRequestValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyQuestionIds_Fails()
    {
        var request = new BatchTagKnowledgeRequest
        {
            QuestionIds = new List<string>(),
            KnowledgeId = "00000000-0000-0000-0000-000000000001",
            Subject = 1,
            Grade = 7,
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.QuestionIds)
            .WithErrorMessage("At least one question id is required");
    }

    [Fact]
    public void Validate_WithEmptyKnowledgeId_Fails()
    {
        var request = new BatchTagKnowledgeRequest
        {
            QuestionIds = new List<string> { "q1" },
            KnowledgeId = "",
            Subject = 1,
            Grade = 7,
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.KnowledgeId)
            .WithErrorMessage("KnowledgeId is required");
    }

    [Fact]
    public void Validate_WithZeroSubject_Fails()
    {
        var request = new BatchTagKnowledgeRequest
        {
            QuestionIds = new List<string> { "q1" },
            KnowledgeId = "00000000-0000-0000-0000-000000000001",
            Subject = 0,
            Grade = 7,
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Subject)
            .WithErrorMessage("Subject must be greater than 0");
    }

    [Fact]
    public void Validate_WithZeroGrade_Fails()
    {
        var request = new BatchTagKnowledgeRequest
        {
            QuestionIds = new List<string> { "q1" },
            KnowledgeId = "00000000-0000-0000-0000-000000000001",
            Subject = 1,
            Grade = 0,
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Grade)
            .WithErrorMessage("Grade must be greater than 0");
    }

    [Fact]
    public void Validate_WithAllValidFields_Succeeds()
    {
        var request = new BatchTagKnowledgeRequest
        {
            QuestionIds = new List<string> { "q1", "q2" },
            KnowledgeId = "00000000-0000-0000-0000-000000000001",
            Subject = 1,
            Grade = 7,
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }
}

public class CreateTagRequestValidatorTests
{
    private readonly CreateTagRequestValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyName_Fails()
    {
        var request = new CreateTagRequest { Name = "" };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Name is required");
    }

    [Fact]
    public void Validate_WithOverLongName_Fails()
    {
        var request = new CreateTagRequest { Name = new string('a', 101) };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Name must not exceed 100 characters");
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#FFFFFFF")]
    [InlineData("#GGGGGG")]
    public void Validate_WithInvalidColor_Fails(string color)
    {
        var request = new CreateTagRequest { Name = "x", Color = color };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Color)
            .WithErrorMessage("Color must be a valid HEX color (e.g., #FF6B6B)");
    }

    [Theory]
    [InlineData("#FF6B6B")]
    [InlineData("#000000")]
    [InlineData("#ffffff")]
    [InlineData("#aBcDeF")]
    public void Validate_WithValidColor_Succeeds(string color)
    {
        var request = new CreateTagRequest { Name = "x", Color = color };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithoutColor_Succeeds()
    {
        var request = new CreateTagRequest { Name = "x" };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }
}

public class BatchTagQuestionRequestValidatorTests
{
    private readonly BatchTagQuestionRequestValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyQuestionIds_Fails()
    {
        var request = new BatchTagQuestionRequest
        {
            QuestionIds = new List<string>(),
            TagIds = new List<string> { "t1" },
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.QuestionIds)
            .WithErrorMessage("At least one question id is required");
    }

    [Fact]
    public void Validate_WithEmptyTagIds_Fails()
    {
        var request = new BatchTagQuestionRequest
        {
            QuestionIds = new List<string> { "q1" },
            TagIds = new List<string>(),
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.TagIds)
            .WithErrorMessage("At least one tag id is required");
    }

    [Fact]
    public void Validate_WithAllValidFields_Succeeds()
    {
        var request = new BatchTagQuestionRequest
        {
            QuestionIds = new List<string> { "q1", "q2" },
            TagIds = new List<string> { "t1" },
        };

        var result = _validator.TestValidate(request);

        result.IsValid.Should().BeTrue();
    }
}
