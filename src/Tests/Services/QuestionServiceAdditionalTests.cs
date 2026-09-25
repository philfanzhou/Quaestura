using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Quaestura.Database.Entity;
using Quaestura.Domain.Services;
using Xunit;

namespace Quaestura.Tests.Services;

public class QuestionServiceAdditionalTests : TestBase
{
    private readonly QuestionService _questionService;

    public QuestionServiceAdditionalTests()
    {
        _questionService = new QuestionService(_dbContext);
    }

    [Fact]
    public async Task AddAsync_NullEntity_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _questionService.AddAsync("user", null!, null!, new List<string>(), 1, 1));
    }

    [Fact]
    public async Task AddAsync_EmptyContentAndNoPictures_ThrowsArgumentNullException()
    {
        var question = new Question { Subject = 1, Grade = 1 };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _questionService.AddAsync("user", question, null!, new List<string>(), 1, 1));
    }

    [Fact]
    public async Task AddAsync_WithOnlyPictures_NoContent_CreatesQuestion()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var picturePaths = new List<string> { "img1.jpg" };

        var (resultQuestion, resultContent) = await _questionService.AddAsync("user", question, null, picturePaths, 1, 1);

        Assert.NotNull(resultQuestion);
        Assert.Null(resultContent);
        Assert.Contains("img1.jpg", resultQuestion.PicturePaths);
    }

    [Fact]
    public async Task AddAsync_SetsCreatedAtAndUpdatedAt()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Time test" };
        var beforeAdd = DateTimeOffset.UtcNow;

        var (resultQuestion, _) = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        Assert.True(resultQuestion.CreatedAt >= beforeAdd);
        Assert.True(resultQuestion.UpdatedAt >= beforeAdd);
    }

    [Fact]
    public async Task AddAsync_SetsUserIdAndSubjectAndGrade()
    {
        var question = new Question { Subject = 3, Grade = 5 };
        var content = new QuestionContent { Content = "Fields test" };

        var (resultQuestion, _) = await _questionService.AddAsync("testUser", question, content, new List<string>(), 3, 5);

        Assert.Equal("testUser", resultQuestion.UserId);
        Assert.Equal(3, resultQuestion.Subject);
        Assert.Equal(5, resultQuestion.Grade);
    }

    [Fact]
    public async Task AddAsync_Content_SetsQuestionId()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Content link test" };

        var (resultQuestion, resultContent) = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        Assert.Equal(resultQuestion.Id, resultContent!.QuestionId);
    }

    [Fact]
    public async Task UpdateAsync_NonExistingQuestion_ReturnsFalse()
    {
        var question = new Question { Id = Guid.NewGuid(), Subject = 1, Grade = 1 };

        var result = await _questionService.UpdateAsync(question, null);

        Assert.False(result);
    }

    [Fact]
    public async Task UpdateAsync_WithPicturePaths_UpdatesPicturePaths()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Picture update test" };
        var added = await _questionService.AddAsync("user", question, content, new List<string> { "old.jpg" }, 1, 1);

        var newPaths = new List<string> { "new1.jpg", "new2.jpg" };
        var result = await _questionService.UpdateAsync(added.question, null, newPaths);

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Contains("new1.jpg", retrieved.Value.question.PicturePaths);
        Assert.Contains("new2.jpg", retrieved.Value.question.PicturePaths);
    }

    [Fact]
    public async Task UpdateAsync_WithEmptyPicturePaths_ClearsPicturePaths()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Clear pictures test" };
        var added = await _questionService.AddAsync("user", question, content, new List<string> { "old.jpg" }, 1, 1);

        var result = await _questionService.UpdateAsync(added.question, null, new List<string>());

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Null(retrieved.Value.question.PicturePaths);
    }

    [Fact]
    public async Task UpdateAsync_AddsContentWhenNoneExists()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var picturePaths = new List<string> { "img.jpg" };
        var added = await _questionService.AddAsync("user", question, null, picturePaths, 1, 1);

        var newContent = new QuestionContent { Content = "Newly added content" };
        var result = await _questionService.UpdateAsync(added.question, newContent);

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Newly added content", retrieved.Value.content?.Content);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesLevelAndType()
    {
        var question = new Question { Subject = 1, Grade = 1, Level = 1, Type = 1 };
        var content = new QuestionContent { Content = "Level type test" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        added.question.Level = 5;
        added.question.Type = 3;
        var result = await _questionService.UpdateAsync(added.question, null);

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(5, retrieved.Value.question.Level);
        Assert.Equal(3, retrieved.Value.question.Type);
    }

    [Fact]
    public async Task UpdateAsync_PreservesContentWhenNullProvided()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Original", CorrectAnswer = "A", Analysis = "Analysis" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var result = await _questionService.UpdateAsync(added.question, null);

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Original", retrieved.Value.content?.Content);
        Assert.Equal("A", retrieved.Value.content?.CorrectAnswer);
    }

    [Fact]
    public async Task UpdateAsync_PartialContentUpdate_OnlyUpdatesProvidedFields()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Original", CorrectAnswer = "A", Analysis = "Original analysis" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        var partialUpdate = new QuestionContent { Content = "Updated content" };
        var result = await _questionService.UpdateAsync(added.question, partialUpdate);

        Assert.True(result);
        var retrieved = await _questionService.GetAsync(added.question.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated content", retrieved.Value.content?.Content);
        Assert.Equal("A", retrieved.Value.content?.CorrectAnswer);
        Assert.Equal("Original analysis", retrieved.Value.content?.Analysis);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeleted_ReturnsFalse()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "Double delete test" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        await _questionService.DeleteAsync(added.question.Id);
        var result = await _questionService.DeleteAsync(added.question.Id);

        Assert.False(result);
    }

    [Fact]
    public async Task SearchAsync_ByType_ReturnsMatchingQuestions()
    {
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Type = 1 }, new QuestionContent { Content = "Type1" }, new List<string>(), 1, 1);
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Type = 2 }, new QuestionContent { Content = "Type2" }, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync(null, null, 2, 1, 1, 1, 10);

        Assert.Equal(1, result.totalCount);
        Assert.Equal("Type2", result.items[0].Content?.Content);
    }

    [Fact]
    public async Task SearchAsync_InvalidPageDefaultsTo1()
    {
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, new QuestionContent { Content = "Q1" }, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync(null, null, null, 1, 1, 0, 10);

        Assert.Equal(1, result.totalCount);
    }

    [Fact]
    public async Task SearchAsync_InvalidSizeDefaultsTo10()
    {
        for (int i = 0; i < 5; i++)
        {
            await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, new QuestionContent { Content = $"Q{i}" }, new List<string>(), 1, 1);
        }

        var result = await _questionService.SearchAsync(null, null, null, 1, 1, 1, 0);

        Assert.Equal(5, result.totalCount);
        Assert.Equal(5, result.items.Count);
    }

    [Fact]
    public async Task SearchAsync_SizeOver100_CappedTo100()
    {
        var result = await _questionService.SearchAsync(null, null, null, 1, 1, 1, 200);

        Assert.Empty(result.items);
    }

    [Fact]
    public async Task SearchAsync_CombinedFilters_ReturnsCorrectResults()
    {
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Level = 1, Type = 1 }, new QuestionContent { Content = "Math easy" }, new List<string>(), 1, 1);
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Level = 2, Type = 1 }, new QuestionContent { Content = "Math hard" }, new List<string>(), 1, 1);
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1, Level = 1, Type = 2 }, new QuestionContent { Content = "Math essay" }, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync(null, 1, 1, 1, 1, 1, 10);

        Assert.Equal(1, result.totalCount);
        Assert.Equal("Math easy", result.items[0].Content?.Content);
    }

    [Fact]
    public async Task GetByIdsAsync_EmptyList_ReturnsEmptyList()
    {
        var result = await _questionService.GetByIdsAsync(new List<Guid>());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByIdsAsync_NonExistingIds_ReturnsEmptyList()
    {
        var result = await _questionService.GetByIdsAsync(new List<Guid> { Guid.NewGuid() });

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByIdsAsync_MixedExistingAndNonExisting_ReturnsOnlyExisting()
    {
        var q = new Question { Subject = 1, Grade = 1 };
        var c = new QuestionContent { Content = "Existing" };
        var added = await _questionService.AddAsync("user", q, c, new List<string>(), 1, 1);

        var result = await _questionService.GetByIdsAsync(new List<Guid> { added.question.Id, Guid.NewGuid() });

        Assert.Single(result);
        Assert.Equal("Existing", result[0].Content?.Content);
    }

    [Fact]
    public async Task GetAsync_AfterDelete_ReturnsNull()
    {
        var question = new Question { Subject = 1, Grade = 1 };
        var content = new QuestionContent { Content = "To be deleted" };
        var added = await _questionService.AddAsync("user", question, content, new List<string>(), 1, 1);

        await _questionService.DeleteAsync(added.question.Id);
        var result = await _questionService.GetAsync(added.question.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_KeywordCaseInsensitive_ReturnsMatches()
    {
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, new QuestionContent { Content = "Mathematics Problem" }, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync("mathematics", null, null, 1, 1, 1, 10);

        Assert.Equal(1, result.totalCount);
    }

    [Fact]
    public async Task SearchAsync_NoMatchingResults_ReturnsEmptyList()
    {
        await _questionService.AddAsync("user", new Question { Subject = 1, Grade = 1 }, new QuestionContent { Content = "Existing" }, new List<string>(), 1, 1);

        var result = await _questionService.SearchAsync("nonexistent", null, null, 1, 1, 1, 10);

        Assert.Equal(0, result.totalCount);
        Assert.Empty(result.items);
    }
}
