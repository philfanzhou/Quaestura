using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.QuestionBank.Database.Entity;
using Ruoyu.Study.QuestionBank.Domain.Services;
using Xunit;

namespace QuestionBank.Test.Services;

public class KnowledgeServiceAdditionalTests : TestBase
{
    private readonly KnowledgeService _knowledgeService;

    public KnowledgeServiceAdditionalTests()
    {
        _knowledgeService = new KnowledgeService(_dbContext);
    }

    [Fact]
    public async Task AddAsync_DuplicateName_ReturnsDuplicateFlag()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "SameName", Subject = 1, Grade = 1 }, "user1");

        var (success, isDuplicate) = await _knowledgeService.AddAsync(
            new Knowledge { Name = "SameName", Subject = 2, Grade = 2 }, "user2");

        Assert.False(success);
        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task AddAsync_SetsCreatedByAndCreatedAt()
    {
        var knowledge = new Knowledge { Name = "CreatedByTest", Subject = 1, Grade = 1 };
        var beforeAdd = DateTimeOffset.UtcNow;

        await _knowledgeService.AddAsync(knowledge, "creator1");

        Assert.Equal("creator1", knowledge.CreatedBy);
        Assert.True(knowledge.CreatedAt >= beforeAdd);
    }

    [Fact]
    public async Task AddAsync_WithParentId_CreatesChildKnowledge()
    {
        var parent = new Knowledge { Name = "Parent", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(parent, "user");

        var child = new Knowledge { Name = "Child", Subject = 1, Grade = 1, ParentId = parent.Id };
        var (success, isDuplicate) = await _knowledgeService.AddAsync(child, "user");

        Assert.True(success);
        Assert.False(isDuplicate);
        Assert.Equal(parent.Id, child.ParentId);
    }

    [Fact]
    public async Task GetChildrenAsync_WithParentId_ReturnsOnlyChildren()
    {
        var parent = new Knowledge { Name = "ParentForChildren", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(parent, "user");

        var child1 = new Knowledge { Name = "ChildA", Subject = 1, Grade = 1, ParentId = parent.Id };
        var child2 = new Knowledge { Name = "ChildB", Subject = 1, Grade = 1, ParentId = parent.Id };
        await _knowledgeService.AddAsync(child1, "user");
        await _knowledgeService.AddAsync(child2, "user");

        var root = new Knowledge { Name = "RootNode", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(root, "user");

        var children = await _knowledgeService.GetChildrenAsync(parent.Id.ToString());

        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(parent.Id, c.ParentId));
    }

    [Fact]
    public async Task GetChildrenAsync_NullParentId_ReturnsRootNodes()
    {
        var root1 = new Knowledge { Name = "Root1", Subject = 1, Grade = 1 };
        var root2 = new Knowledge { Name = "Root2", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(root1, "user");
        await _knowledgeService.AddAsync(root2, "user");

        var parent = new Knowledge { Name = "SubParent", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(parent, "user");
        var child = new Knowledge { Name = "SubChild", Subject = 1, Grade = 1, ParentId = parent.Id };
        await _knowledgeService.AddAsync(child, "user");

        var roots = await _knowledgeService.GetChildrenAsync(null);

        Assert.Equal(3, roots.Count);
        Assert.All(roots, r => Assert.Null(r.ParentId));
    }

    [Fact]
    public async Task GetChildrenAsync_EmptyStringParentId_ReturnsRootNodes()
    {
        var root = new Knowledge { Name = "EmptyStringRoot", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(root, "user");

        var result = await _knowledgeService.GetChildrenAsync("");

        Assert.Single(result);
        Assert.Null(result[0].ParentId);
    }

    [Fact]
    public async Task GetChildrenAsync_OrdersByName()
    {
        var parent = new Knowledge { Name = "OrderParent", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(parent, "user");

        await _knowledgeService.AddAsync(new Knowledge { Name = "Zebra", Subject = 1, Grade = 1, ParentId = parent.Id }, "user");
        await _knowledgeService.AddAsync(new Knowledge { Name = "Apple", Subject = 1, Grade = 1, ParentId = parent.Id }, "user");
        await _knowledgeService.AddAsync(new Knowledge { Name = "Mango", Subject = 1, Grade = 1, ParentId = parent.Id }, "user");

        var children = await _knowledgeService.GetChildrenAsync(parent.Id.ToString());

        Assert.Equal("Apple", children[0].Name);
        Assert.Equal("Mango", children[1].Name);
        Assert.Equal("Zebra", children[2].Name);
    }

    [Fact]
    public async Task UpdateAsync_NonExistingKnowledge_ReturnsFalse()
    {
        var knowledge = new Knowledge { Id = Guid.NewGuid(), Name = "Ghost" };

        var (success, isReferenced, wouldCreateCycle) = await _knowledgeService.UpdateAsync(knowledge, "user");

        Assert.False(success);
        Assert.False(isReferenced);
        Assert.False(wouldCreateCycle);
    }

    [Fact]
    public async Task UpdateAsync_SetsUpdatedByAndUpdatedAt()
    {
        var knowledge = new Knowledge { Name = "UpdateTimestamp", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "creator");
        var beforeUpdate = DateTimeOffset.UtcNow;

        knowledge.Name = "UpdatedTimestamp";
        await _knowledgeService.UpdateAsync(knowledge, "updater");

        var updated = await _dbContext.Knowledges.FirstAsync();
        Assert.Equal("updater", updated.UpdatedBy);
        Assert.True(updated.UpdatedAt >= beforeUpdate);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesAllFields()
    {
        var knowledge = new Knowledge { Name = "Original", Subject = 1, Grade = 1, Description = "Old desc" };
        await _knowledgeService.AddAsync(knowledge, "user");

        knowledge.Name = "Updated";
        knowledge.Subject = 2;
        knowledge.Grade = 3;
        knowledge.Description = "New desc";
        await _knowledgeService.UpdateAsync(knowledge, "user2");

        var updated = await _dbContext.Knowledges.FirstAsync();
        Assert.Equal("Updated", updated.Name);
        Assert.Equal(2, updated.Subject);
        Assert.Equal(3, updated.Grade);
        Assert.Equal("New desc", updated.Description);
    }

    [Fact]
    public async Task UpdateAsync_WithParentId_UpdatesParent()
    {
        var parent = new Knowledge { Name = "NewParent", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(parent, "user");

        var child = new Knowledge { Name = "MovableChild", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(child, "user");

        child.ParentId = parent.Id;
        var (success, _, _) = await _knowledgeService.UpdateAsync(child, "user");

        Assert.True(success);
        var updated = await _dbContext.Knowledges.FirstAsync(k => k.Name == "MovableChild");
        Assert.Equal(parent.Id, updated.ParentId);
    }

    [Fact]
    public async Task DeleteAsync_NonExistingKnowledge_ReturnsFalse()
    {
        var (success, isReferenced) = await _knowledgeService.DeleteAsync(Guid.NewGuid().ToString());

        Assert.False(success);
        Assert.False(isReferenced);
    }

    [Fact]
    public async Task DeleteAsync_InvalidGuidFormat_ThrowsFormatException()
    {
        await Assert.ThrowsAsync<FormatException>(
            () => _knowledgeService.DeleteAsync("not-a-guid"));
    }

    [Fact]
    public async Task DeleteAsync_AfterDeletion_GetAsyncReturnsNull()
    {
        var knowledge = new Knowledge { Name = "DeleteVerify", Subject = 1, Grade = 1 };
        await _knowledgeService.AddAsync(knowledge, "user");

        await _knowledgeService.DeleteAsync(knowledge.Id.ToString());
        var result = await _knowledgeService.GetAsync(knowledge.Id.ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_InvalidGuidFormat_ThrowsFormatException()
    {
        await Assert.ThrowsAsync<FormatException>(
            () => _knowledgeService.GetAsync("not-a-guid"));
    }

    [Fact]
    public async Task GetByGradeAndSubjectAsync_Pagination_Works()
    {
        for (int i = 0; i < 15; i++)
        {
            await _knowledgeService.AddAsync(new Knowledge { Name = $"GS_{i}", Subject = 1, Grade = 1 }, "user");
        }

        var page1 = await _knowledgeService.GetByGradeAndSubjectAsync(1, 1, 1, 10);
        var page2 = await _knowledgeService.GetByGradeAndSubjectAsync(1, 1, 2, 10);

        Assert.Equal(15, page1.totalCount);
        Assert.Equal(2, page1.totalPages);
        Assert.Equal(10, page1.items.Count);
        Assert.Equal(5, page2.items.Count);
    }

    [Fact]
    public async Task GetByGradeAndSubjectAsync_InvalidPageDefaultsTo1()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "PageDefault", Subject = 1, Grade = 1 }, "user");

        var result = await _knowledgeService.GetByGradeAndSubjectAsync(1, 1, 0, 10);

        Assert.Equal(1, result.totalCount);
    }

    [Fact]
    public async Task GetByGradeAndSubjectAsync_InvalidSizeDefaultsTo10()
    {
        for (int i = 0; i < 5; i++)
        {
            await _knowledgeService.AddAsync(new Knowledge { Name = $"SizeDefault_{i}", Subject = 1, Grade = 1 }, "user");
        }

        var result = await _knowledgeService.GetByGradeAndSubjectAsync(1, 1, 1, 0);

        Assert.Equal(5, result.totalCount);
        Assert.Equal(5, result.items.Count);
    }

    [Fact]
    public async Task GetByGradeAndSubjectAsync_NoMatchingData_ReturnsEmpty()
    {
        var result = await _knowledgeService.GetByGradeAndSubjectAsync(99, 99, 1, 10);

        Assert.Equal(0, result.totalCount);
        Assert.Empty(result.items);
    }

    [Fact]
    public async Task GetAllAsync_OrdersByName()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "Zebra", Subject = 1, Grade = 1 }, "user");
        await _knowledgeService.AddAsync(new Knowledge { Name = "Apple", Subject = 1, Grade = 1 }, "user");
        await _knowledgeService.AddAsync(new Knowledge { Name = "Mango", Subject = 1, Grade = 1 }, "user");

        var (items, _, _) = await _knowledgeService.GetAllAsync(1, 10);

        Assert.Equal("Apple", items[0].Name);
        Assert.Equal("Mango", items[1].Name);
        Assert.Equal("Zebra", items[2].Name);
    }

    [Fact]
    public async Task GetAllAsync_InvalidPageDefaultsTo1()
    {
        await _knowledgeService.AddAsync(new Knowledge { Name = "PageTest", Subject = 1, Grade = 1 }, "user");

        var (items, _, totalCount) = await _knowledgeService.GetAllAsync(0, 10);

        Assert.Equal(1, totalCount);
        Assert.Single(items);
    }

    [Fact]
    public async Task GetAllAsync_InvalidSizeDefaultsTo10()
    {
        for (int i = 0; i < 5; i++)
        {
            await _knowledgeService.AddAsync(new Knowledge { Name = $"SizeTest_{i}", Subject = 1, Grade = 1 }, "user");
        }

        var (items, _, totalCount) = await _knowledgeService.GetAllAsync(1, 0);

        Assert.Equal(5, totalCount);
        Assert.Equal(5, items.Count);
    }
}
