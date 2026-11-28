using Microsoft.EntityFrameworkCore;
using Quaestura.Database.Entity;
using Quaestura.Domain.Services;
using Xunit;

namespace Quaestura.Tests.Database;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class KnowledgeReferenceTests(PostgreSqlMigrationFixture fixture)
{
    [Fact]
    public async Task ConcurrentDeletesOfSharedQuestions_ReleaseThePointAndAllowRebinding()
    {
        var database = await fixture.CreateDatabaseAsync();
        try
        {
            await using var writer = fixture.CreateContext(database);
            await writer.Database.MigrateAsync();
            var first = new Question { Grade = 7, Subject = 2 };
            var second = new Question { Grade = 7, Subject = 2 };
            var third = new Question { Grade = 7, Subject = 2 };
            var point = new Knowledge { Name = "Geometry", Grade = 7, Subject = 2 };
            writer.AddRange(first, second, third, point);
            await writer.SaveChangesAsync();
            var bindings = new QuestionKnowledgeService(writer);
            Assert.Equal(1, await bindings.TagAsync(first.Id, [(point.Id, 1)], 2, 7));
            Assert.Equal(1, await bindings.TagAsync(second.Id, [(point.Id, 1)], 2, 7));

            await using var left = fixture.CreateContext(database);
            await using var right = fixture.CreateContext(database);
            var deletions = await Task.WhenAll(
                new QuestionService(left).DeleteAsync(first.Id),
                new QuestionService(right).DeleteAsync(second.Id));
            Assert.All(deletions, deleted => Assert.True(deleted));
            Assert.False(await writer.Knowledges.Select(k => k.IsReferenced).SingleAsync());
            Assert.False(await writer.QuestionKnowledges.AnyAsync());

            // This context still tracks the point from before the two deletions.
            Assert.Equal(1, await bindings.TagAsync(third.Id, [(point.Id, 1)], 2, 7));
            Assert.True(await writer.Knowledges.Select(k => k.IsReferenced).SingleAsync());
            Assert.True(await bindings.RemoveAllTagsAsync(third.Id));
            Assert.False(await writer.Knowledges.Select(k => k.IsReferenced).SingleAsync());
        }
        finally
        {
            await fixture.DropDatabaseAsync(database);
        }
    }
}
