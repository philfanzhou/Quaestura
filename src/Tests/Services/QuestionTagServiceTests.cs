using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database.Entity;
using Quaestura.Domain.Services;
using Xunit;

namespace Quaestura.Tests.Services;

public class QuestionTagServiceTests : TestBase
{
    private readonly TagService _tagService;
    private readonly QuestionService _questionService;
    private readonly QuestionTagService _questionTagService;

    public QuestionTagServiceTests()
    {
        _tagService = new TagService(_dbContext);
        _questionService = new QuestionService(_dbContext);
        _questionTagService = new QuestionTagService(_dbContext);
    }

    private async Task<Guid> SeedQuestionAsync()
    {
        var question = new Question
        {
            Level = 1,
            Type = 1,
            Subject = 1,
            Grade = 7,
        };
        var content = new QuestionContent { Content = "test" };
        var (entity, _) = await _questionService.AddAsync("u", question, content, new System.Collections.Generic.List<string>(), 1, 7);
        return entity.Id;
    }

    private async Task<Tag> SeedTagAsync(string name)
    {
        var tag = new Tag { Name = name };
        await _tagService.AddAsync(tag, "u");
        return tag;
    }

    [Fact]
    public async Task TagAsync_NewRelation_CreatesAndIncrementsUsage()
    {
        var qId = await SeedQuestionAsync();
        var tag = await SeedTagAsync("t1");

        var inserted = await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { tag.Id });

        Assert.Equal(1, inserted);

        var reloaded = await _tagService.GetAsync(tag.Id);
        Assert.Equal(1, reloaded!.UsageCount);
    }

    [Fact]
    public async Task TagAsync_DuplicateRelation_Skipped()
    {
        var qId = await SeedQuestionAsync();
        var tag = await SeedTagAsync("t1");

        await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { tag.Id });
        var inserted = await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { tag.Id });

        Assert.Equal(0, inserted);

        var reloaded = await _tagService.GetAsync(tag.Id);
        Assert.Equal(1, reloaded!.UsageCount);
    }

    [Fact]
    public async Task TagAsync_MultipleTags_AllInserted()
    {
        var qId = await SeedQuestionAsync();
        var t1 = await SeedTagAsync("t1");
        var t2 = await SeedTagAsync("t2");
        var t3 = await SeedTagAsync("t3");

        var inserted = await _questionTagService.TagAsync(qId,
            new System.Collections.Generic.List<Guid> { t1.Id, t2.Id, t3.Id });

        Assert.Equal(3, inserted);
    }

    [Fact]
    public async Task TagAsync_NonExistingQuestion_NoOp()
    {
        var tag = await SeedTagAsync("t1");

        var inserted = await _questionTagService.TagAsync(Guid.NewGuid(),
            new System.Collections.Generic.List<Guid> { tag.Id });

        Assert.Equal(0, inserted);
        var reloaded = await _tagService.GetAsync(tag.Id);
        Assert.Equal(0, reloaded!.UsageCount);
    }

    [Fact]
    public async Task TagAsync_NonExistingTag_Skipped()
    {
        var qId = await SeedQuestionAsync();

        var inserted = await _questionTagService.TagAsync(qId,
            new System.Collections.Generic.List<Guid> { Guid.NewGuid() });

        Assert.Equal(0, inserted);
    }

    [Fact]
    public async Task UntagAsync_ExistingRelation_RemovesAndDecrements()
    {
        var qId = await SeedQuestionAsync();
        var tag = await SeedTagAsync("t1");
        await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { tag.Id });

        var removed = await _questionTagService.UntagAsync(qId,
            new System.Collections.Generic.List<Guid> { tag.Id });

        Assert.Equal(1, removed);
        var reloaded = await _tagService.GetAsync(tag.Id);
        Assert.Equal(0, reloaded!.UsageCount);
    }

    [Fact]
    public async Task UntagAsync_NonExistingRelation_NoOp()
    {
        var qId = await SeedQuestionAsync();
        var tag = await SeedTagAsync("t1");

        var removed = await _questionTagService.UntagAsync(qId,
            new System.Collections.Generic.List<Guid> { tag.Id });

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task RemoveAllAsync_RemovesAllAndDecrementsAll()
    {
        var qId = await SeedQuestionAsync();
        var t1 = await SeedTagAsync("t1");
        var t2 = await SeedTagAsync("t2");
        await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { t1.Id, t2.Id });

        var removed = await _questionTagService.RemoveAllAsync(qId);

        Assert.Equal(2, removed);
        Assert.Equal(0, (await _tagService.GetAsync(t1.Id))!.UsageCount);
        Assert.Equal(0, (await _tagService.GetAsync(t2.Id))!.UsageCount);
    }

    [Fact]
    public async Task RemoveAllAsync_NoRelations_ReturnsZero()
    {
        var qId = await SeedQuestionAsync();

        var removed = await _questionTagService.RemoveAllAsync(qId);

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task GetByQuestionAsync_ReturnsAllRelations()
    {
        var qId = await SeedQuestionAsync();
        var t1 = await SeedTagAsync("t1");
        var t2 = await SeedTagAsync("t2");
        await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { t1.Id, t2.Id });

        var relations = await _questionTagService.GetByQuestionAsync(qId);

        Assert.Equal(2, relations.Count);
        Assert.All(relations, r => Assert.Equal(qId, r.QuestionId));
        Assert.All(relations, r => Assert.NotNull(r.Tag));
    }

    [Fact]
    public async Task GetQuestionIdsByTagAsync_ReturnsPagedIds()
    {
        var tag = await SeedTagAsync("popular");
        for (var i = 0; i < 5; i++)
        {
            var qId = await SeedQuestionAsync();
            await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { tag.Id });
        }

        var (ids, total) = await _questionTagService.GetQuestionIdsByTagAsync(tag.Id, 1, 3);

        Assert.Equal(5, total);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public async Task GetQuestionIdsByTagAsync_EmptyTag_ReturnsZero()
    {
        var tag = await SeedTagAsync("unused");

        var (ids, total) = await _questionTagService.GetQuestionIdsByTagAsync(tag.Id, 1, 10);

        Assert.Equal(0, total);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task TagAsync_DuplicateTagIdsInList_Deduplicated()
    {
        var qId = await SeedQuestionAsync();
        var tag = await SeedTagAsync("t1");

        var inserted = await _questionTagService.TagAsync(qId,
            new System.Collections.Generic.List<Guid> { tag.Id, tag.Id, tag.Id });

        Assert.Equal(1, inserted);
        Assert.Equal(1, (await _tagService.GetAsync(tag.Id))!.UsageCount);
    }

    [Fact]
    public async Task UntagAsync_DecrementsMultipleAffectedTags()
    {
        var qId = await SeedQuestionAsync();
        var t1 = await SeedTagAsync("t1");
        var t2 = await SeedTagAsync("t2");
        await _questionTagService.TagAsync(qId, new System.Collections.Generic.List<Guid> { t1.Id, t2.Id });

        var removed = await _questionTagService.UntagAsync(qId,
            new System.Collections.Generic.List<Guid> { t1.Id, t2.Id });

        Assert.Equal(2, removed);
        Assert.Equal(0, (await _tagService.GetAsync(t1.Id))!.UsageCount);
        Assert.Equal(0, (await _tagService.GetAsync(t2.Id))!.UsageCount);
    }
}
