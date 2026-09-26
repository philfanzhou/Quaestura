using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;
using Xunit;

namespace Quaestura.Tests.Database;

public class ModelSnapshotTests
{
    [Fact]
    public void Migrations_CoverCurrentModel()
    {
        // No connection is opened; the provider is only needed to build the relational model.
        var options = new DbContextOptionsBuilder<QuaesturaDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused")
            .Options;
        using var context = new QuaesturaDbContext(options);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "every model change must ship with an EF Core migration in src/Database/Migrations");
    }
}
