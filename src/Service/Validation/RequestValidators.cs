using FluentValidation;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Contract.Protos;

namespace Ruoyu.Study.QuestionBank.Service.Validation;

public class QuestionValidator : AbstractValidator<Question>
{
    public QuestionValidator()
    {
        RuleFor(x => x.PicturePaths)
            .NotEmpty().WithMessage("题目图片必须至少有一张");

        RuleFor(x => x.Level)
            .GreaterThanOrEqualTo(0).WithMessage("难度等级不能为负数");

        RuleFor(x => x.Type)
            .GreaterThanOrEqualTo(0).WithMessage("题目类型不能为负数");

        RuleFor(x => x.Subject)
            .GreaterThan(0).WithMessage("学科必须大于0");

        RuleFor(x => x.Grade)
            .GreaterThan(0).WithMessage("年级必须大于0");
    }
}

public class KnowledgeValidator : AbstractValidator<Knowledge>
{
    public KnowledgeValidator()
    {
        RuleFor(x => x.Subject)
            .GreaterThan(0).WithMessage("学科必须大于0");

        RuleFor(x => x.Grade)
            .GreaterThan(0).WithMessage("年级必须大于0");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("知识点名称不能为空");
    }
}
