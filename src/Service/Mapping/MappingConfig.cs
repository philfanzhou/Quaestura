using System;
using Google.Protobuf;
using Mapster;
using Ruoyu.Study.QuestionBank.Contract.Protos;
using Ruoyu.Study.QuestionBank.Database.Entity;

namespace Ruoyu.Study.QuestionBank.Service.Mapping;

public static class MappingConfig
{
    public static void RegisterMappings()
    {
        // Question 映射
        TypeAdapterConfig<Question, QuestionDto>.NewConfig()
            .Map(dest => dest.Id, src => src.Id.ToString())
            .Map(dest => dest.CreatedAt, src => src.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))
            .Map(dest => dest.UpdatedAt, src => src.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"))
            .Map(dest => dest.UserId, src => src.UserId ?? "")
            .Map(dest => dest.StudentId, src => src.StudentId ?? "")
            .Map(dest => dest.MistakeId, src => src.MistakeId ?? "");

        TypeAdapterConfig<QuestionDto, Question>.NewConfig()
            .Map(dest => dest.Id, src => string.IsNullOrEmpty(src.Id) ? Guid.Empty : Guid.Parse(src.Id))
            .Map(dest => dest.UserId, src => src.UserId)
            .Map(dest => dest.StudentId, src => src.StudentId)
            .Map(dest => dest.MistakeId, src => string.IsNullOrEmpty(src.MistakeId) ? null : src.MistakeId)
            .Ignore(dest => dest.CreatedAt)
            .Ignore(dest => dest.UpdatedAt)
            .Ignore(dest => dest.PicturePaths)
            .Ignore(dest => dest.QuestionKnowledges);

        // QuestionContent 映射
        TypeAdapterConfig<QuestionContentDto, QuestionContent>.NewConfig()
            .Map(dest => dest.Content, src => src.Content)
            .Map(dest => dest.CorrectAnswer, src => src.CorrectAnswer)
            .Map(dest => dest.Analysis, src => src.Analysis);

        // Knowledge 映射
        TypeAdapterConfig<Knowledge, KnowledgeDto>.NewConfig()
            .Map(dest => dest.Id, src => src.Id.ToString())
            .Map(dest => dest.ParentId, src => src.ParentId.HasValue ? src.ParentId.Value.ToString() : "")
            .Map(dest => dest.Name, src => src.Name)
            .Map(dest => dest.Description, src => src.Description ?? "")
            .Map(dest => dest.CreatedBy, src => src.CreatedBy ?? "")
            .Map(dest => dest.CreatedAt, src => src.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"))
            .Map(dest => dest.IsReferenced, src => src.IsReferenced)
            .Map(dest => dest.Subject, src => src.Subject ?? 0)
            .Map(dest => dest.Grade, src => src.Grade ?? 0)
            .Map(dest => dest.UpdatedBy, src => src.UpdatedBy ?? "")
            .Map(dest => dest.UpdatedAt, src => MapNullableDateTime(src.UpdatedAt));

        TypeAdapterConfig<KnowledgeDto, Knowledge>.NewConfig()
            .Map(dest => dest.Id, src => string.IsNullOrEmpty(src.Id) ? Guid.Empty : Guid.Parse(src.Id))
            .Map(dest => dest.ParentId, src => string.IsNullOrEmpty(src.ParentId) ? (Guid?)null : Guid.Parse(src.ParentId))
            .Map(dest => dest.Name, src => src.Name)
            .Map(dest => dest.Description, src => src.Description)
            .Map(dest => dest.IsReferenced, src => src.IsReferenced)
            .Map(dest => dest.Subject, src => src.Subject)
            .Map(dest => dest.Grade, src => src.Grade)
            .Map(dest => dest.UpdatedBy, src => src.UpdatedBy)
            .Ignore(nameof(KnowledgeDto.CreatedBy))
            .Ignore(nameof(KnowledgeDto.CreatedAt))
            .Ignore(dest => dest.CreatedAt)
            .Ignore(dest => dest.Children)
            .Ignore(dest => dest.QuestionKnowledges);

        // QuestionKnowledge 映射
        TypeAdapterConfig<QuestionKnowledge, QuestionKnowledgeDto>.NewConfig()
            .Map(dest => dest.Id, src => src.Id.ToString())
            .Map(dest => dest.QuestionId, src => src.QuestionId.ToString())
            .Map(dest => dest.KnowledgeId, src => src.KnowledgeId.ToString())
            .Map(dest => dest.Weight, src => src.Weight)
            .Map(dest => dest.Subject, src => src.Knowledge.Subject)
            .Map(dest => dest.Grade, src => src.Knowledge.Grade);
    }

    private static string MapNullableDateTime(DateTime? dateTime)
    {
        return dateTime.HasValue ? dateTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
    }

    private static string? MapEmptyString(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? MapZeroToNull(int value)
    {
        return value == 0 ? null : value;
    }
}
