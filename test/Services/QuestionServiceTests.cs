using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Xunit;

namespace QuestionBank.Test.Services;

public class QuestionServiceTests : TestBase
{
    private readonly QuestionService _questionService;

    public QuestionServiceTests()
    {
        _questionService = new QuestionService(_dbContext);
    }

    [Fact]
    public async Task AddAsync_ValidQuestion_ReturnsQuestion()
    {
        var question = new Question
        {
            Subject = 1,
            Grade = 1,
            Level = 1,
            Type = 1
        };
        var content = new QuestionContent
        {
            Content = "Test question content"
        };

        var (resultQuestion, resultContent) = await _questionService.AddAsync("user123", question, content, new List<string>(), 1, 1);

        Assert.NotNull(resultQuestion);
        Assert.NotEqual(Guid.Empty, resultQuestion.Id);
        Assert.NotNull(resultContent);
        Assert.Equal("Test question content", resultContent.Content);
        Assert.Equal("user123", resultQuestion.UserId);
        Assert.Equal(1, resultQuestion.Subject);
        Assert.Equal(1, resultQuestion.Grade);
    }

    [Fact]
    public async Task AddAsync_WithPictures_StoresPicturePaths()
    {
        var question = new Question
        {
            Subject = 1,
            Grade = 1,
        };
        var content = new QuestionContent
        {
            Content = "Question with pictures"
        };
        var picturePaths = new List<string> { "path1.jpg", "path2.jpg" };

        var (resultQuestion, resultContent) = await _questionService.AddAsync("user123", question, content, picturePaths, 1, 1);

        Assert.NotNull(resultQuestion);
        Assert.Contains("path1.jpg", resultQuestion.PicturePaths);
        Assert.Contains("path2.jpg", resultQuestion.PicturePaths);
    }

    [Fact]
    public async Task GetAsync_ExistingQuestion_ReturnsQuestionWithContent()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Get test" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var result = await _questionService.GetAsync(added.question.Id);

        Assert.NotNull(result);
        Assert.Equal(added.question.Id, result.Value.question.Id);
        Assert.Equal("Get test", result.Value.content?.Content);
    }

    [Fact]
    public async Task GetAsync_NonExistingQuestion_ReturnsNull()
    {
        var result = await _questionService.GetAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_BySubjectAndGrade_ReturnsMatchingQuestions()
    {
        var q1Content = new QuestionContent { Content = "Q1" };
        var q2Content = new QuestionContent { Content = "Q2" };
        var q3Content = new QuestionContent { Content = "Q3" };
        
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, q1Content, new List<string>(), 1, 1);
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 2 }, q2Content, new List<string>(), 1, 2);
        await _questionService.AddAsync("user", new Question { Subject = 2, Grade = 1 }, q3Content, new List<string>(), 2, 1);

        var result = await _questionService.SearchAsync(null, null, null, 1, 1, 1, 10);

        Assert.Equal(1, result.totalCount);
        Assert.Single(result.items);
        Assert.Equal("Q1", result.items[0].Content?.Content);
    }

    [Fact]
    public async Task SearchAsync_ByLevel_ReturnsMatchingQuestions()
    {
        var easyContent = new QuestionContent { Content = "Easy" };
        var hardContent = new QuestionContent { Content = "Hard" };
        
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Level = 1 }, easyContent, new List<string>(), 1, 1);
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Level = 5 }, hardContent, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync(null, 5, null, 1, 1, 1, 10);

        Assert.Equal(1, result.totalCount);
        Assert.Equal("Hard", result.items[0].Content?.Content);
    }

    [Fact]
    public async Task SearchAsync_ByKeyword_ReturnsMatchingQuestions()
    {
        var mathContent = new QuestionContent { Content = "What is 2+2 in mathematics?" };
        var scienceContent = new QuestionContent { Content = "Explain photosynthesis" };
        
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, mathContent, new List<string>(), 1, 1);
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, scienceContent, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync("mathematics", null, null, 1, 1, 1, 10);

        Assert.Equal(1, result.totalCount);
        Assert.Contains("mathematics", result.items[0].Content?.Content);
    }

    [Fact]
    public async Task SearchAsync_Pagination_Works()
    {
        for (int i = 0; i < 15; i++)
        {
            var content = new QuestionContent { Content = $"Q{i}" };
            await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, content, new List<string>(), 1, 1);
        }

        var page1 = await _questionService.SearchAsync(null, null, null, 1, 1, 1, 10);
        var page2 = await _questionService.SearchAsync(null, null, null, 1, 1, 2, 10);

        Assert.Equal(15, page1.totalCount);
        Assert.Equal(2, page1.totalPages);
        Assert.Equal(10, page1.items.Count);
        Assert.Equal(5, page2.items.Count);
    }

    [Fact]
    public async Task DeleteAsync_ExistingQuestion_ReturnsTrue()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "ToDelete" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var result = await _questionService.DeleteAsync(added.question.Id);

        Assert.True(result);
        var deleted = await _questionService.GetAsync(added.question.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task DeleteAsync_NonExistingQuestion_ReturnsFalse()
    {
        var result = await _questionService.DeleteAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task UpdateAsync_ExistingQuestion_UpdatesSuccessfully()
    {
        var question = new Question { Subject = 1, Grade = 1, Level = 1 };
        var content = new QuestionContent { Content = "Original content", CorrectAnswer = "A" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var updateContent = new QuestionContent { Content = "Updated content", CorrectAnswer = "B" };
        added.question.Level = 3;

        var result = await _questionService.UpdateAsync(added.question, updateContent);

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(3, retrieved.Value.question.Level);
        Assert.Equal("Updated content", retrieved.Value.content?.Content);
        Assert.Equal("B", retrieved.Value.content?.CorrectAnswer);
    }

    [Fact]
    public async Task GetByIdsAsync_MultipleIds_ReturnsAllQuestions()
    {
        var q1 = new Question { Subject = 1, Grade = 1 };
        var c1 = new QuestionContent { Content = "First" };
        var q2 = new Question { Subject = 1, Grade = 1 };
        var c2 = new QuestionContent { Content = "Second" };
        
        var added1 = await _questionService.AddAsync("user", q1, c1, new List<string>(), 1, 1);
        var added2 = await _questionService.AddAsync("user", q2, c2, new List<string>(), 1, 1);

        var ids = new List<Guid> { added1.question.Id, added2.question.Id };
        var result = await _questionService.GetByIdsAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.Content?.Content == "First");
        Assert.Contains(result, x => x.Content?.Content == "Second");
    }
}
