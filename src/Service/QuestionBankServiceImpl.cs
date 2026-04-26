using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Core;
using Mapster;
using Ruoyu.Study.QuestionBank.Contract.Protos;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Ruoyu.Study.QuestionBank.Service.Validation;

namespace Ruoyu.Study.QuestionBank.Service;

public class QuestionBankServiceImpl : QuestionBankGrpcService.QuestionBankGrpcServiceBase
{
    private readonly IQuestionService _questionService;
    private readonly IKnowledgeService _knowledgeService;
    private readonly IQuestionKnowledgeService _questionKnowledgeService;
    private readonly IOssQuestionService _ossQuestionService;

    public QuestionBankServiceImpl(
        IQuestionService questionService,
        IKnowledgeService knowledgeService,
        IQuestionKnowledgeService questionKnowledgeService,
        IOssQuestionService ossQuestionService)
    {
        _questionService = questionService;
        _knowledgeService = knowledgeService;
        _questionKnowledgeService = questionKnowledgeService;
        _ossQuestionService = ossQuestionService;
    }

    #region Question Operations

    public override async Task<QuestionDto> GetQuestion(GetQuestionRequest request, ServerCallContext context)
    {
        var result = await _questionService.GetAsync(Guid.Parse(request.QuestionId));
        if (result == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Question not found"));
        }
        var dto = MapToQuestionDto(result.Value.question, result.Value.content);
        ConvertQuestionPicturePathsToUrls(dto);
        return dto;
    }

    public override async Task<QuestionPageResult> SearchQuestions(SearchQuestionsRequest request, ServerCallContext context)
    {
        var result = await _questionService.SearchAsync(
            string.IsNullOrWhiteSpace(request.Keyword) ? null : request.Keyword,
            request.Level > 0 ? request.Level : null,
            request.Type > 0 ? request.Type : null,
            request.Subject, request.Grade, request.Page, request.Size);

        var dtos = result.items.Select(item => MapToQuestionDto(item.Question, item.Content)).ToList();
        ConvertQuestionPicturePathsToUrls(dtos);

        return new QuestionPageResult
        {
            Items = { dtos },
            TotalPage = result.totalPages,
            TotalCount = result.totalCount
        };
    }

    public override async Task<BoolResponse> AddOrUpdateQuestion(AddOrUpdateQuestionRequest request, ServerCallContext context)
    {
        List<string>? uploadedPaths = null;
        List<(Stream stream, string fileName, string contentType)>? streams = null;

        try
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "User ID is required"));
            }

            var question = new Question();
            var content = new QuestionContent
            {
                Content = request.Content?.Content,
                CorrectAnswer = request.Content?.CorrectAnswer,
                Analysis = request.Content?.Analysis
            };

            List<string> picturePaths = new List<string>();

            if (request.Pictures.Count > 0)
            {
                ImageValidationHelper.ValidateImages(request.Pictures);

                streams = request.Pictures.Select((bytes, idx) =>
                {
                    var stream = new MemoryStream(bytes.ToByteArray());
                    return ((Stream)stream, $"q_{Guid.NewGuid():N}_{idx}.jpg", "image/jpeg");
                }).ToList();

                uploadedPaths = await _ossQuestionService.UploadPicturesAsync(streams);
                picturePaths = uploadedPaths;
            }

            if (!string.IsNullOrWhiteSpace(request.Question.Id))
            {
                var questionId = Guid.Parse(request.Question.Id);
                var existing = await _questionService.GetAsync(questionId);
                if (existing != null)
                {
                    var existingQuestion = existing.Value.question;
                    existingQuestion.Level = request.Question.Level;
                    existingQuestion.Type = request.Question.Type;
                    existingQuestion.Grade = request.Grade;
                    existingQuestion.Subject = request.Subject;
                    existingQuestion.StudentId = request.Question.StudentId;
                    existingQuestion.MistakeId = request.Question.MistakeId;
                    existingQuestion.UpdatedAt = DateTime.UtcNow;

                    await _questionService.UpdateAsync(existingQuestion, content, picturePaths.Count > 0 ? picturePaths : null);
                    return new BoolResponse { Success = true };
                }
            }

            var result = await _questionService.AddAsync(request.UserId, question, content, picturePaths, request.Subject, request.Grade);
            return new BoolResponse { Success = result.question != null, ErrorMessage = result.question == null ? "Failed to add question" : null };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (uploadedPaths != null && uploadedPaths.Count > 0)
            {
                await _ossQuestionService.DeletePicturesAsync(uploadedPaths);
            }
            return new BoolResponse { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            if (streams != null)
            {
                foreach (var (stream, _, _) in streams)
                {
                    stream.Dispose();
                }
            }
        }
    }

    public override async Task<BoolResponse> DeleteQuestion(IdRequest request, ServerCallContext context)
    {
        var question = await _questionService.GetAsync(Guid.Parse(request.Id));
        if (question == null)
        {
            return new BoolResponse { Success = false, ErrorMessage = "Question not found" };
        }

        var picturePaths = !string.IsNullOrEmpty(question.Value.question.PicturePaths)
            ? JsonSerializer.Deserialize<List<string>>(question.Value.question.PicturePaths) ?? new List<string>()
            : new List<string>();

        var success = await _questionService.DeleteAsync(Guid.Parse(request.Id));

        if (success && picturePaths.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await _ossQuestionService.DeletePicturesAsync(picturePaths); }
                catch { }
            });
        }

        return new BoolResponse { Success = success, ErrorMessage = success ? null : "Delete failed" };
    }

    public override async Task<QuestionDtoList> GetQuestions(GetQuestionsRequest request, ServerCallContext context)
    {
        var result = await _questionService.SearchAsync(
            null,
            request.Level > 0 ? request.Level : null,
            request.Type > 0 ? request.Type : null,
            request.Subject, request.Grade, request.Page, request.Size);

        var dtos = result.items.Select(item => MapToQuestionDto(item.Question, item.Content)).ToList();
        ConvertQuestionPicturePathsToUrls(dtos);

        return new QuestionDtoList
        {
            Questions = { dtos }
        };
    }

    #endregion

    #region Knowledge Operations

    public override async Task<KnowledgeDto> GetKnowledge(IdRequest request, ServerCallContext context)
    {
        var knowledge = await _knowledgeService.GetAsync(request.Id);
        if (knowledge == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Knowledge not found"));
        }
        return knowledge.Adapt<KnowledgeDto>();
    }

    public override async Task<KnowledgeDtoList> GetAllKnowledges(GetAllKnowledgesRequest request, ServerCallContext context)
    {
        var knowledges = await _knowledgeService.GetChildrenAsync(
            string.IsNullOrEmpty(request.ParentId) ? null : request.ParentId);

        return new KnowledgeDtoList
        {
            Knowledges = { knowledges.Adapt<List<KnowledgeDto>>() }
        };
    }

    public override async Task<BoolResponse> AddOrUpdateKnowledge(AddOrUpdateKnowledgeRequest request, ServerCallContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "User ID is required"));
            }

            var knowledge = request.Knowledge.Adapt<Knowledge>();

            bool success;
            if (string.IsNullOrEmpty(knowledge.Id.ToString()) || knowledge.Id == Guid.Empty)
            {
                var result = await _knowledgeService.AddAsync(knowledge, request.UserId);
                success = result.success;
                if (result.isDuplicate)
                {
                    return new BoolResponse { Success = false, ErrorMessage = "Knowledge already exists" };
                }
            }
            else
            {
                var result = await _knowledgeService.UpdateAsync(knowledge, request.UserId);
                success = result.success;
                if (result.isReferenced)
                {
                    return new BoolResponse { Success = false, ErrorMessage = "Knowledge is referenced and cannot be updated" };
                }
                if (result.wouldCreateCycle)
                {
                    return new BoolResponse { Success = false, ErrorMessage = "Cannot update: would create circular parent reference" };
                }
            }

            return new BoolResponse { Success = success, ErrorMessage = success ? null : "Operation failed" };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BoolResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<BoolResponse> DeleteKnowledge(IdRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _knowledgeService.DeleteAsync(request.Id);
            if (result.isReferenced)
            {
                return new BoolResponse { Success = false, ErrorMessage = "Knowledge is referenced and cannot be deleted" };
            }
            return new BoolResponse { Success = result.success, ErrorMessage = result.success ? null : "Delete failed" };
        }
        catch (Exception ex)
        {
            return new BoolResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<KnowledgeDtoList> GetKnowledgesByGradeAndSubject(GetKnowledgesByGradeAndSubjectRequest request, ServerCallContext context)
    {
        var result = await _knowledgeService.GetByGradeAndSubjectAsync(
            request.Grade, request.Subject, request.Page, request.Size);

        return new KnowledgeDtoList
        {
            Knowledges = { result.items.Adapt<List<KnowledgeDto>>() }
        };
    }

    public override async Task<KnowledgeDtoList> GetKnowledgeByNameLike(GetKnowledgeByNameLikeRequest request, ServerCallContext context)
    {
        var knowledges = await _knowledgeService.GetLikeAsync(request.Name);
        return new KnowledgeDtoList
        {
            Knowledges = { knowledges.Adapt<List<KnowledgeDto>>() }
        };
    }

    public override async Task<KnowledgePageResult> GetAllKnowledgesWithPaging(GetAllKnowledgesWithPagingRequest request, ServerCallContext context)
    {
        var result = await _knowledgeService.GetAllAsync(request.Page, request.Size);
        return new KnowledgePageResult
        {
            Items = { result.items.Adapt<List<KnowledgeDto>>() },
            TotalPage = result.totalPages,
            TotalCount = result.totalCount
        };
    }

    #endregion

    #region Question-Knowledge Relations

    public override async Task<BoolResponse> BatchTagKnowledge(BatchTagKnowledgeRequest request, ServerCallContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "User ID is required"));
            }

            var knowledgeId = Guid.Parse(request.KnowledgeId);
            var tags = new List<(Guid knowledgeId, double weight)> { (knowledgeId, 1.0) };

            int totalSuccess = 0;
            foreach (var qIdStr in request.QuestionIds)
            {
                var qId = Guid.Parse(qIdStr);
                var result = await _questionKnowledgeService.TagAsync(qId, tags, request.Subject, request.Grade);
                if (result > 0) totalSuccess++;
            }

            return new BoolResponse
            {
                Success = totalSuccess > 0,
                ErrorMessage = totalSuccess > 0 ? null : "Failed to tag knowledge to any question"
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BoolResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<QuestionKnowledgeDtoList> GetQuestionKnowledges(GetQuestionKnowledgesRequest request, ServerCallContext context)
    {
        var relations = await _questionKnowledgeService.GetByQuestionAsync(Guid.Parse(request.QuestionId));
        return new QuestionKnowledgeDtoList
        {
            Relations = { relations.Adapt<List<QuestionKnowledgeDto>>() }
        };
    }

    public override async Task<QuestionPageResult> GetQuestionsByKnowledge(GetQuestionsByKnowledgeRequest request, ServerCallContext context)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var size = request.Size < 1 ? 10 : request.Size;

        var (questionIds, totalCount) = await _questionKnowledgeService.GetQuestionsByTagAsync(Guid.Parse(request.KnowledgeId), page, size);

        if (totalCount == 0)
        {
            return new QuestionPageResult
            {
                Items = { },
                TotalPage = 0,
                TotalCount = 0
            };
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)size);
        var questions = await _questionService.GetByIdsAsync(questionIds);

        var dtos = questions.Select(item => MapToQuestionDto(item.Question, item.Content)).ToList();
        ConvertQuestionPicturePathsToUrls(dtos);

        return new QuestionPageResult
        {
            Items = { dtos },
            TotalPage = totalPages,
            TotalCount = totalCount
        };
    }

    public override async Task<BoolResponse> RemoveAllQuestionKnowledges(RemoveAllQuestionKnowledgesRequest request, ServerCallContext context)
    {
        try
        {
            var success = await _questionKnowledgeService.RemoveAllTagsAsync(Guid.Parse(request.QuestionId));
            return new BoolResponse { Success = success, ErrorMessage = success ? null : "Failed to remove all question knowledges" };
        }
        catch (Exception ex)
        {
            return new BoolResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    #endregion

    #region Helpers

    private QuestionDto MapToQuestionDto(Question question, QuestionContent? content)
    {
        var dto = question.Adapt<QuestionDto>();
        dto.Id = question.Id.ToString();
        if (content != null)
        {
            dto.Content = content.Content ?? string.Empty;
            dto.CorrectAnswer = content.CorrectAnswer ?? string.Empty;
            dto.Analysis = content.Analysis ?? string.Empty;
        }
        if (!string.IsNullOrEmpty(question.PicturePaths))
        {
            var paths = JsonSerializer.Deserialize<List<string>>(question.PicturePaths) ?? new List<string>();
            dto.PicturePaths.AddRange(paths);
        }
        return dto;
    }

    private void ConvertQuestionPicturePathsToUrls(QuestionDto dto)
    {
        if (dto.PicturePaths.Count > 0)
        {
            var urls = _ossQuestionService.GetPictureUrls(dto.PicturePaths.ToList());
            dto.PicturePaths.Clear();
            dto.PicturePaths.AddRange(urls);
        }
    }

    private List<QuestionDto> ConvertQuestionPicturePathsToUrls(List<QuestionDto> dtos)
    {
        foreach (var dto in dtos)
        {
            ConvertQuestionPicturePathsToUrls(dto);
        }
        return dtos;
    }

    #endregion
}
