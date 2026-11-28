using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Quaestura.Domain.Services;
using Quaestura.Service.Models;

namespace Quaestura.Service.Endpoints;

internal static class RelatedQuestionPage
{
    internal static async Task<IResult> CreateAsync(
        List<Guid> questionIds, int totalCount, int page, int size,
        IQuestionService questionService, IOssQuestionService ossService,
        ILoggerFactory? loggerFactory, string loggerCategory)
    {
        if (totalCount == 0)
        {
            return Results.Ok(new
            {
                success = true,
                data = new List<object>(),
                total = 0,
                page,
                size,
                totalPages = 0,
            });
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)size);
        var questions = await questionService.GetByIdsAsync(questionIds);
        var responses = questions.Select(item => QuestionResponse.From(item.Question, item.Content)).ToList();

        var logger = loggerFactory?.CreateLogger(loggerCategory);
        foreach (var dto in responses)
        {
            if (dto.PicturePaths.Count == 0) continue;
            try
            {
                var urls = await ossService.GetPictureUrls(dto.PicturePaths.ToList());
                dto.PicturePaths.Clear();
                dto.PicturePaths.AddRange(urls);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to convert picture paths to URLs");
            }
        }

        return Results.Ok(new
        {
            success = true,
            data = responses,
            total = totalCount,
            page,
            size,
            totalPages,
        });
    }
}
