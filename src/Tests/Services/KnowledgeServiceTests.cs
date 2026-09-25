using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database.Entity;
using Quaestura.Domain.Services;
using Xunit;

namespace Quaestura.Tests.Services;

public class KnowledgeServiceTests : TestBase
{
    private readonly KnowledgeService _knowledgeService;

    public KnowledgeServiceTests()
    {
        _knowledgeService = new KnowledgeService(_dbContext);
    }

    [Fact]
    public async Task AddAsync_NewKnowledge_ReturnsSuccess()
    {
        var knowledge = new Knowledge { Name = "Test Knowledge", Subject = 1, Grade = 1 };

        var (success, isDuplicate) = await _knowledgeService.AddAsync(knowledge, "user1");

        Assert.True(success);
        Assert.False(isDuplicate);
        Assert.NotEqual(Guid.Empty, knowledge.Id);
        Assert.Equal("user1", knowledge.CreatedBy);
    }

    [Fact]
    public async Task AddAsync_DuplicateName_ReturnsDuplicate()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "Duplicate", Subject = 1, Grade = 1 }, "user1");

        var (success, isDuplicate) = await _knowledgeService.AddAsync(
            new Knowledge { Name = "DifferentName", Subject = 1, Grade = 1 }, "user2");

        Assert.True(success);
        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task GetAsync_ExistingId_ReturnsKnowledge()
    {
        var knowledge = new Knowledge { Name = "GetTest", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "user");

        var result = await _knowledgeService.GetAsync(knowledge.Id.ToString());

        Assert.NotNull(result);
        Assert.Equal("GetTest", result.Name);
    }

    [Fact]
    public async Task GetAsync_NonExistingId_ReturnsNull()
    {
        var result = await _knowledgeService.GetAsync(Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetChildrenAsync_ReturnsRootOnly()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "Root1", Subject = 1, Grade = 1 }, "u");
        await _knowledgeService.AddAsync(new Knowledge { Name = "Root2", Subject = 1, Grade = 1 }, "u");

        var parent = new Knowledge { Name = "Parent1", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(parent, "u");

        var child = new Knowledge { Name = "Child1", Subject = 1, Grade = 1, ParentId = parent.Id };
        await _knowledgeService.AddAsync(child, "u");

        var result = await _knowledgeService.GetChildrenAsync(null);

        Assert.DoesNotContain(result, k => k.Name == "Child1");
    }

    [Fact]
    public async Task UpdateAsync_ExistingKnowledge_UpdatesFields()
    {
        var knowledge = new Knowledge { Name = "Original", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "u");

        knowledge.Name = "Updated";
        var (success, isReferenced, wouldCreateCycle) = await _knowledgeService.UpdateAsync(knowledge, "user2");

        Assert.True(success);
        Assert.False(isReferenced);
        Assert.False(wouldCreateCycle);
        var updated = await _dbContext.Knowledges.FirstAsync();
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("user2", updated.UpdatedBy);
    }

    [Fact]
    public async Task UpdateAsync_ReferencedKnowledge_ReturnsReferencedFlag()
    {
        var knowledge = new Knowledge { Name = "RefTest", Subject = 1, Grade = 1, IsReferenced = true };
        await _knowledgeService.AddAsync(knowledge, "u");

        var (success, isReferenced, wouldCreateCycle) = await _knowledgeService.UpdateAsync(knowledge, "u");

        Assert.False(success);
        Assert.True(isReferenced);
        Assert.False(wouldCreateCycle);
    }

    [Fact]
    public async Task DeleteAsync_ExistingNotReferenced_ReturnsSuccess()
    {
        var knowledge = new Knowledge { Name = "ToDelete", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "u");

        var (success, isReferenced) = await _knowledgeService.DeleteAsync(knowledge.Id.ToString());

        Assert.True(success);
        Assert.False(isReferenced);
        Assert.Null(await _dbContext.Knowledges.FindAsync(knowledge.Id));
    }

    [Fact]
    public async Task DeleteAsync_ReferencedKnowledge_ReturnsReferencedFlag()
    {
        var knowledge = new Knowledge { Name = "CantDelete", Subject = 1, Grade = 1, IsReferenced = true };
        await _knowledgeService.AddAsync(knowledge, "u");

        var (success, isReferenced) = await _knowledgeService.DeleteAsync(knowledge.Id.ToString());

        Assert.False(success);
        Assert.True(isReferenced);
    }

    [Fact]
    public async Task GetByGradeAndSubjectAsync_FiltersCorrectly()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "K1", Subject = 1, Grade = 1 }, "u");
        await _knowledgeService.AddAsync(new Knowledge { Name = "K2", Subject = 1, Grade = 2 }, "u");
        await _knowledgeService.AddAsync(new Knowledge { Name = "K3", Subject = 2, Grade = 1 }, "u");

        var result = await _knowledgeService.GetByGradeAndSubjectAsync(1, 1, 1, 10);

        Assert.Single(result.items);
        Assert.Equal("K1", result.items[0].Name);
    }

    [Fact]
    public async Task GetAllAsync_Pagination_Works()
    {
        for (int i = 0; i < 15; i++)
            await _knowledgeService.AddAsync(new Knowledge { Name = $"K{i}", Subject = 1, Grade = 1 }, "u");

        var page1 = await _knowledgeService.GetAllAsync(1, 10);
        var page2 = await _knowledgeService.GetAllAsync(2, 10);

        Assert.Equal(15, page1.totalCount);
        Assert.Equal(2, page1.totalPages);
        Assert.Equal(10, page1.items.Count);
        Assert.Equal(5, page2.items.Count);
    }
}
