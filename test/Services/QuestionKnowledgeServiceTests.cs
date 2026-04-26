using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Xunit;

namespace QuestionBank.Test.Services;

public class QuestionKnowledgeServiceTests : TestBase
{
    private readonly IQuestionKnowledgeService _questionKnowledgeService;
    private readonly QuestionService _questionService;
    private readonly KnowledgeService _knowledgeService;

    public QuestionKnowledgeServiceTests()
    {
        _questionKnowledgeService = new QuestionKnowledgeService(_dbContext);
        _questionService = new QuestionService(_dbContext);
        _knowledgeService = new KnowledgeService(_dbContext);
    }

    private async Task<(Question q, Knowledge k)> SetupQuestionAndKnowledge()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Test" };
        var (q, _) = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var knowledge = new Knowledge { Name = "TestKnowledge", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "user");

        var k = (await _dbContext.Knowledges.FirstAsync());
        return (q!, k);
    }

    [Fact]
    public async Task TagAsync_ValidData_InsertsTag()
    {
        // Arrange
        var (question, knowledge) = await SetupQuestionAndKnowledge();

        // Act
        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };
        var result = await _questionKnowledgeService.TagAsync(question.Id, tags, 1, 1);

        // Assert
        Assert.Equal(1, result);
        var relation = await _dbContext.QuestionKnowledges.FirstOrDefaultAsync();
        Assert.NotNull(relation);
        Assert.Equal(question.Id, relation.QuestionId);
        Assert.Equal(knowledge.Id, relation.KnowledgeId);
    }

    [Fact]
    public async Task TagAsync_DuplicateTag_DoesNotInsertAgain()
    {
        var (question, knowledge) = await SetupQuestionAndKnowledge();
        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };
        await _questionKnowledgeService.TagAsync(question.Id, tags, 1, 1);

        var result = await _questionKnowledgeService.TagAsync(question.Id, tags, 1, 1);

        Assert.Equal(0, result);
        var count = await _dbContext.QuestionKnowledges.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TagAsync_MismatchedSubjectGrade_ReturnsZero()
    {
        var (question, knowledge) = await SetupQuestionAndKnowledge();
        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };

        var result = await _questionKnowledgeService.TagAsync(question.Id, tags, 2, 2);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task TagAsync_UnknownKnowledgeId_ReturnsZero()
    {
        var (question, _) = await SetupQuestionAndKnowledge();
        var tags = new List<(Guid knowledgeId, double weight)> { (Guid.NewGuid(), 1.0) };

        var result = await _questionKnowledgeService.TagAsync(question.Id, tags, 1, 1);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task TagAsync_MultipleKnowledges_InsertsAll()
    {
        var (q, _) = await SetupQuestionAndKnowledge();

        var secondKnowledge = new Knowledge { Name = "SecondKnowledge", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(secondKnowledge, "user");
        var k2 = await _dbContext.Knowledges.FirstAsync(k => k.Name == "SecondKnowledge");

        var tags = new List<(Guid knowledgeId, double weight)>
        {
            ((await _dbContext.Knowledges.FirstAsync(k => k.Name == "TestKnowledge")).Id, 1.0),
            (k2.Id, 0.8)
        };

        var result = await _questionKnowledgeService.TagAsync(q.Id, tags, 1, 1);

        Assert.Equal(2, result);
        var count = await _dbContext.QuestionKnowledges.CountAsync(qk => qk.QuestionId == q.Id);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task TagAsync_IsReferenced_Updated()
    {
        var (_, knowledge) = await SetupQuestionAndKnowledge();
        Assert.False(knowledge.IsReferenced);

        var (q, _) = await SetupQuestionAndKnowledge();
        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };

        await _questionKnowledgeService.TagAsync(q.Id, tags, 1, 1);

        var updatedKnowledge = await _dbContext.Knowledges.FindAsync(knowledge.Id);
        Assert.True(updatedKnowledge!.IsReferenced);
    }

    [Fact]
    public async Task GetByQuestionAsync_ReturnsAllQuestionKnowledges()
    {
        var (question, knowledge) = await SetupQuestionAndKnowledge();
        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };
        await _questionKnowledgeService.TagAsync(question.Id, tags, 1, 1);

        var result = await _questionKnowledgeService.GetByQuestionAsync(question.Id);

        Assert.Single(result);
        Assert.Equal(question.Id, result[0].QuestionId);
    }

    [Fact]
    public async Task GetQuestionsByTagAsync_ReturnsQuestionIds()
    {
        var (question, knowledge) = await SetupQuestionAndKnowledge();
        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };
        await _questionKnowledgeService.TagAsync(question.Id, tags, 1, 1);

        var result = await _questionKnowledgeService.GetQuestionsByTagAsync(knowledge.Id);

        Assert.Single(result);
        Assert.Equal(question.Id, result[0]);
    }

    [Fact]
    public async Task GetQuestionsByTagAsync_Pagination_Works()
    {
        var knowledge = new Knowledge { Name = "PaginationKnowledge", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "user");
        var k = await _dbContext.Knowledges.FirstAsync(kn => kn.Name == "PaginationKnowledge");

        for (int i = 0; i < 15; i++)
        {
            var q = new Question { Subject = 1, Grade = 1 };
            var c = new QuestionContent { Content = $"Q{i}" };
            var (added, _) = await _questionService.AddAsync("user", q, c, new List<string>(), 1, 1);
            var tags = new List<(Guid knowledgeId, double weight)> { (k.Id, 1.0) };
            await _questionKnowledgeService.TagAsync(added.Id, tags, 1, 1);
        }

        var (page1Ids, page1Total) = await _questionKnowledgeService.GetQuestionsByTagAsync(k.Id, 1, 10);
        var (page2Ids, page2Total) = await _questionKnowledgeService.GetQuestionsByTagAsync(k.Id, 2, 10);

        Assert.Equal(15, page1Total);
        Assert.Equal(10, page1Ids.Count);
        Assert.Equal(5, page2Ids.Count);
    }

    [Fact]
    public async Task RemoveAllTagsAsync_DeletesRelationsAndResetsIsReferenced()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "TestQ" };
        var (q, _) = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var knowledge = new Knowledge { Name = "TagTestKnowledge2", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "user");
        var k = await _dbContext.Knowledges.FirstAsync(kn => kn.Name == "TagTestKnowledge2");

        var tags = new List<(Guid knowledgeId, double weight)> { (k.Id, 1.0) };
        await _questionKnowledgeService.TagAsync(q.Id, tags, 1, 1);

        Assert.True(k.IsReferenced);

        var result = await _questionKnowledgeService.RemoveAllTagsAsync(q.Id);

        Assert.True(result);
        var count = await _dbContext.QuestionKnowledges.CountAsync();
        Assert.Equal(0, count);

        await _dbContext.Entry(k).ReloadAsync();
        var refreshedKnowledge = await _dbContext.Knowledges.FindAsync(k.Id);
        Assert.False(refreshedKnowledge!.IsReferenced);
    }

    [Fact]
    public async Task RemoveAllTagsAsync_NoRelations_ReturnsTrue()
    {
        var (question, _) = await SetupQuestionAndKnowledge();

        var result = await _questionKnowledgeService.RemoveAllTagsAsync(question.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task RemoveAllTagsAsync_OnlyDeletesSpecificQuestionRelations()
    {
        var (q1, knowledge) = await SetupQuestionAndKnowledge();
        var q2 = new Question { Subject = 1, Grade = 1 };
        var c2 = new QuestionContent { Content = "Q2" };
        var (q2Added, _) = await _questionService.AddAsync("user", q2, c2, new List<string>(), 1, 1);

        var tags = new List<(Guid knowledgeId, double weight)> { (knowledge.Id, 1.0) };
        await _questionKnowledgeService.TagAsync(q1.Id, tags, 1, 1);
        await _questionKnowledgeService.TagAsync(q2Added.Id, tags, 1, 1);

        await _questionKnowledgeService.RemoveAllTagsAsync(q1.Id);

        var count = await _dbContext.QuestionKnowledges.CountAsync();
        Assert.Equal(1, count);
        var remaining = await _dbContext.QuestionKnowledges.FirstAsync();
        Assert.Equal(q2Added.Id, remaining.QuestionId);
    }
}
