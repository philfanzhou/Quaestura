using FluentAssertions;
using FluentValidation.TestHelper;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Service.Validation;
using Xunit;

namespace QuestionBank.Test.Validation;

public class QuestionValidatorTests
{
    private readonly QuestionValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyPicturePaths_Fails()
    {
        var question = new Question
        {
            PicturePaths = null,
            Level = 1,
            Type = 1,
            Subject = 1,
            Grade = 1
        };

        var result = _validator.TestValidate(question);

        result.ShouldHaveValidationErrorFor(x => x.PicturePaths)
            .WithErrorMessage("题目图片必须至少有一张");
    }

    [Fact]
    public void Validate_WithNegativeLevel_Fails()
    {
        var question = new Question
        {
            PicturePaths = "img.jpg",
            Level = -1,
            Type = 1,
            Subject = 1,
            Grade = 1
        };

        var result = _validator.TestValidate(question);

        result.ShouldHaveValidationErrorFor(x => x.Level)
            .WithErrorMessage("难度等级不能为负数");
    }

    [Fact]
    public void Validate_WithNegativeType_Fails()
    {
        var question = new Question
        {
            PicturePaths = "img.jpg",
            Level = 1,
            Type = -1,
            Subject = 1,
            Grade = 1
        };

        var result = _validator.TestValidate(question);

        result.ShouldHaveValidationErrorFor(x => x.Type)
            .WithErrorMessage("题目类型不能为负数");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveSubject_Fails(int subject)
    {
        var question = new Question
        {
            PicturePaths = "img.jpg",
            Level = 1,
            Type = 1,
            Subject = subject,
            Grade = 1
        };

        var result = _validator.TestValidate(question);

        result.ShouldHaveValidationErrorFor(x => x.Subject)
            .WithErrorMessage("学科必须大于0");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveGrade_Fails(int grade)
    {
        var question = new Question
        {
            PicturePaths = "img.jpg",
            Level = 1,
            Type = 1,
            Subject = 1,
            Grade = grade
        };

        var result = _validator.TestValidate(question);

        result.ShouldHaveValidationErrorFor(x => x.Grade)
            .WithErrorMessage("年级必须大于0");
    }

    [Fact]
    public void Validate_WithAllValidFields_Succeeds()
    {
        var question = new Question
        {
            PicturePaths = "img1.jpg;img2.jpg",
            Level = 5,
            Type = 2,
            Subject = 3,
            Grade = 6
        };

        var result = _validator.TestValidate(question);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMultipleErrors_ReportsAllErrors()
    {
        var question = new Question
        {
            PicturePaths = null,
            Level = -1,
            Type = -1,
            Subject = 0,
            Grade = 0
        };

        var result = _validator.TestValidate(question);

        result.Errors.Should().HaveCount(5);
        result.ShouldHaveValidationErrorFor(x => x.PicturePaths);
        result.ShouldHaveValidationErrorFor(x => x.Level);
        result.ShouldHaveValidationErrorFor(x => x.Type);
        result.ShouldHaveValidationErrorFor(x => x.Subject);
        result.ShouldHaveValidationErrorFor(x => x.Grade);
    }
}

public class KnowledgeValidatorTests
{
    private readonly KnowledgeValidator _validator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveSubject_Fails(int subject)
    {
        var knowledge = new Knowledge
        {
            Name = "test",
            Subject = subject,
            Grade = 1
        };

        var result = _validator.TestValidate(knowledge);

        result.ShouldHaveValidationErrorFor(x => x.Subject)
            .WithErrorMessage("学科必须大于0");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveGrade_Fails(int grade)
    {
        var knowledge = new Knowledge
        {
            Name = "test",
            Subject = 1,
            Grade = grade
        };

        var result = _validator.TestValidate(knowledge);

        result.ShouldHaveValidationErrorFor(x => x.Grade)
            .WithErrorMessage("年级必须大于0");
    }

    [Fact]
    public void Validate_WithEmptyName_Fails()
    {
        var knowledge = new Knowledge
        {
            Name = "",
            Subject = 1,
            Grade = 1
        };

        var result = _validator.TestValidate(knowledge);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("知识点名称不能为空");
    }

    [Fact]
    public void Validate_WithNullName_Fails()
    {
        var knowledge = new Knowledge
        {
            Name = null!,
            Subject = 1,
            Grade = 1
        };

        var result = _validator.TestValidate(knowledge);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("知识点名称不能为空");
    }

    [Fact]
    public void Validate_WithAllValidFields_Succeeds()
    {
        var knowledge = new Knowledge
        {
            Name = "代数",
            Subject = 3,
            Grade = 7
        };

        var result = _validator.TestValidate(knowledge);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithMultipleErrors_ReportsAllErrors()
    {
        var knowledge = new Knowledge
        {
            Name = "",
            Subject = 0,
            Grade = 0
        };

        var result = _validator.TestValidate(knowledge);

        result.Errors.Should().HaveCount(3);
        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.Subject);
        result.ShouldHaveValidationErrorFor(x => x.Grade);
    }
}
