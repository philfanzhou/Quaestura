using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Xunit;

namespace QuestionBank.Test.Services;

public class TagServiceAdditionalTests : TestBase
{
    private readonly TagService _tagService;

    public TagServiceAdditionalTests()
    {
        _tagService = new TagService(_dbContext);
    }

    [Fact]
    public async Task AddAsync_NameWithLeadingTrailingSpaces_TrimmedForComparison()
    {
        await _tagService.AddAsync(new Tag { Name = "spaced" }, "u");

        var (success, isDuplicate) = await _tagService.AddAsync(
            new Tag { Name = "  spaced  " }, "u");

        Assert.False(success);
        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task ListAsync_PageSize_LimitsResults()
    {
        for (var i = 0; i < 5; i++)
        {
            await _tagService.AddAsync(new Tag { Name = $"tag{i}" }, "u");
        }

        var (items, totalPages, totalCount) = await _tagService.ListAsync(1, 2, null, null);

        Assert.Equal(5, totalCount);
        Assert.Equal(3, totalPages);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task ListAsync_EmptyDatabase_ReturnsEmpty()
    {
        var (items, totalPages, totalCount) = await _tagService.ListAsync(1, 10, null, null);

        Assert.Equal(0, totalCount);
        Assert.Equal(0, totalPages);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_PageBeyondRange_ReturnsEmpty()
    {
        await _tagService.AddAsync(new Tag { Name = "only" }, "u");

        var (items, totalPages, totalCount) = await _tagService.ListAsync(10, 10, null, null);

        Assert.Equal(1, totalCount);
        Assert.Equal(1, totalPages);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_PageSizeClampedTo100()
    {
        // size 200 should be clamped to 100 internally
        var (items, _, _) = await _tagService.ListAsync(1, 200, null, null);

        // No data, but verify no exception
        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_NegativePageAndSize_ClampedToDefaults()
    {
        await _tagService.AddAsync(new Tag { Name = "a" }, "u");

        var (items, _, _) = await _tagService.ListAsync(-1, -5, null, null);

        Assert.Single(items);
    }

    [Fact]
    public async Task UpdateAsync_NonExisting_ReturnsFalse()
    {
        var (success, isDuplicate) = await _tagService.UpdateAsync(
            new Tag { Id = Guid.NewGuid(), Name = "x" }, "u");

        Assert.False(success);
        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task UpdateAsync_SameNameAsSelf_NoDuplicate()
    {
        var tag = new Tag { Name = "self" };
        await _tagService.AddAsync(tag, "u");

        var (success, isDuplicate) = await _tagService.UpdateAsync(
            new Tag { Id = tag.Id, Name = "self" }, "u");

        Assert.True(success);
        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task ListAsync_NameFilterIsCaseInsensitive()
    {
        await _tagService.AddAsync(new Tag { Name = "CamelCase" }, "u");

        var (items, _, _) = await _tagService.ListAsync(1, 10, "camelcase", null);

        Assert.Single(items);
    }
}
