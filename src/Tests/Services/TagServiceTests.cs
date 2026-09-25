using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database.Entity;
using Quaestura.Domain.Services;
using Xunit;

namespace Quaestura.Tests.Services;

public class TagServiceTests : TestBase
{
    private readonly TagService _tagService;

    public TagServiceTests()
    {
        _tagService = new TagService(_dbContext);
    }

    [Fact]
    public async Task AddAsync_NewTag_ReturnsSuccess()
    {
        var tag = new Tag { Name = "期中重点", Color = "#FF6B6B" };

        var (success, isDuplicate) = await _tagService.AddAsync(tag, "user1");

        Assert.True(success);
        Assert.False(isDuplicate);
        Assert.NotEqual(Guid.Empty, tag.Id);
        Assert.Equal("user1", tag.CreatedBy);
        Assert.Equal(0, tag.UsageCount);
    }

    [Fact]
    public async Task AddAsync_DuplicateName_ReturnsDuplicate()
    {
        await _tagService.AddAsync(new Tag { Name = "Unique" }, "user1");

        var (success, isDuplicate) = await _tagService.AddAsync(
            new Tag { Name = "Unique" }, "user2");

        Assert.False(success);
        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task AddAsync_DuplicateNameCaseInsensitive_ReturnsDuplicate()
    {
        await _tagService.AddAsync(new Tag { Name = "Unique" }, "user1");

        var (success, isDuplicate) = await _tagService.AddAsync(
            new Tag { Name = "UNIQUE" }, "user2");

        Assert.False(success);
        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task GetAsync_ExistingId_ReturnsTag()
    {
        var tag = new Tag { Name = "GetTest" };
        await _tagService.AddAsync(tag, "user");

        var result = await _tagService.GetAsync(tag.Id);

        Assert.NotNull(result);
        Assert.Equal("GetTest", result!.Name);
    }

    [Fact]
    public async Task GetAsync_NonExistingId_ReturnsNull()
    {
        var result = await _tagService.GetAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllTags()
    {
        await _tagService.AddAsync(new Tag { Name = "A" }, "user");
        await _tagService.AddAsync(new Tag { Name = "B" }, "user");
        await _tagService.AddAsync(new Tag { Name = "C" }, "user");

        var (items, totalPages, totalCount) = await _tagService.ListAsync(1, 10, null, null);

        Assert.Equal(3, totalCount);
        Assert.Equal(1, totalPages);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task ListAsync_WithNameFilter_ReturnsMatches()
    {
        await _tagService.AddAsync(new Tag { Name = "期中重点" }, "user");
        await _tagService.AddAsync(new Tag { Name = "期末重点" }, "user");
        await _tagService.AddAsync(new Tag { Name = "易错题" }, "user");

        var (items, _, _) = await _tagService.ListAsync(1, 10, "重点", null);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task ListAsync_SortByUsageCount_ReturnsDesc()
    {
        var t1 = new Tag { Name = "popular" };
        var t2 = new Tag { Name = "moderate" };
        var t3 = new Tag { Name = "unpopular" };
        await _tagService.AddAsync(t1, "u");
        await _tagService.AddAsync(t2, "u");
        await _tagService.AddAsync(t3, "u");

        // Manually set usage counts
        t1.UsageCount = 100;
        t2.UsageCount = 10;
        t3.UsageCount = 0;
        await _dbContext.SaveChangesAsync();

        var (items, _, _) = await _tagService.ListAsync(1, 10, null, "usageCount");

        Assert.Equal("popular", items[0].Name);
        Assert.Equal("moderate", items[1].Name);
        Assert.Equal("unpopular", items[2].Name);
    }

    [Fact]
    public async Task UpdateAsync_ExistingTag_UpdatesFields()
    {
        var tag = new Tag { Name = "old", Color = "#000000" };
        await _tagService.AddAsync(tag, "u");

        var updated = new Tag { Id = tag.Id, Name = "new", Color = "#FFFFFF", Description = "d" };
        var (success, isDuplicate) = await _tagService.UpdateAsync(updated, "u");

        Assert.True(success);
        Assert.False(isDuplicate);

        var reloaded = await _tagService.GetAsync(tag.Id);
        Assert.Equal("new", reloaded!.Name);
        Assert.Equal("#FFFFFF", reloaded.Color);
        Assert.Equal("d", reloaded.Description);
    }

    [Fact]
    public async Task UpdateAsync_DuplicateNameOnOtherTag_ReturnsDuplicate()
    {
        var t1 = new Tag { Name = "first" };
        var t2 = new Tag { Name = "second" };
        await _tagService.AddAsync(t1, "u");
        await _tagService.AddAsync(t2, "u");

        var conflict = new Tag { Id = t2.Id, Name = "first" };
        var (success, isDuplicate) = await _tagService.UpdateAsync(conflict, "u");

        Assert.False(success);
        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task DeleteAsync_UnreferencedTag_ReturnsSuccess()
    {
        var tag = new Tag { Name = "deleteable" };
        await _tagService.AddAsync(tag, "u");

        var (success, isReferenced) = await _tagService.DeleteAsync(tag.Id);

        Assert.True(success);
        Assert.False(isReferenced);
        Assert.Null(await _tagService.GetAsync(tag.Id));
    }

    [Fact]
    public async Task DeleteAsync_ReferencedTag_ReturnsReferenced()
    {
        var tag = new Tag { Name = "inuse" };
        await _tagService.AddAsync(tag, "u");
        tag.UsageCount = 5;
        await _dbContext.SaveChangesAsync();

        var (success, isReferenced) = await _tagService.DeleteAsync(tag.Id);

        Assert.False(success);
        Assert.True(isReferenced);
    }

    [Fact]
    public async Task DeleteAsync_NonExisting_ReturnsFalse()
    {
        var (success, isReferenced) = await _tagService.DeleteAsync(Guid.NewGuid());

        Assert.False(success);
        Assert.False(isReferenced);
    }
}
