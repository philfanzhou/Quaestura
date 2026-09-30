using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Quaestura.Database;
using ServiceMantle.Migration;
using Xunit;

namespace Quaestura.Tests.Database;

/// <summary>
/// Strict read-only classification of real PostgreSQL states against the two known migration
/// versions, including the historical question(id)-only corruption reproduction that must never
/// be stamped again. Every rejection is proven to perform zero writes.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class MigrationInspectionTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public MigrationInspectionTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _database = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task EmptyDatabase_ClassifiesAsEmpty()
    {
        using var context = _fixture.CreateContext(_database);

        await AssertState(context, MigrationObservationState.Empty);
    }

    [Fact]
    public async Task MissingCatalog_ClassifiesAsEmpty()
    {
        // The target database does not exist; EF Migrate keeps its existing creation behavior
        // (dedicated shared target preparation is tracked separately in #41).
        using var context = _fixture.CreateContext($"missing_{Guid.NewGuid():N}");

        await AssertState(context, MigrationObservationState.Empty);
    }

    [Fact]
    public async Task InitialHistoryOnly_ClassifiesAsPendingMigration_WithoutBaselineRegistration()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyInitialOnlyAsync(context);

        var inspection = await InspectDetailed(context);

        inspection.State.Should().Be(MigrationObservationState.PendingMigration);
        inspection.RequiresInitialBaseline.Should().BeFalse();
    }

    [Fact]
    public async Task FullyMigrated_ClassifiesAsCurrentVersionCompatible()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);

        await AssertState(context, MigrationObservationState.CurrentVersionCompatible);
    }

    [Fact]
    public async Task EnsureCreatedLegacy_WithBothTagTables_ClassifiesAsPendingMigrationRequiringBaseline()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);

        var inspection = await InspectDetailed(context);

        inspection.State.Should().Be(MigrationObservationState.PendingMigration);
        inspection.RequiresInitialBaseline.Should().BeTrue();
    }

    [Fact]
    public async Task LegacyInitialSchemaWithoutHistory_ClassifiesAsPendingMigrationRequiringBaseline()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyInitialOnlyAsync(context);
        await MigrationGoldenStates.DropHistoryAsync(context);

        var inspection = await InspectDetailed(context);

        inspection.State.Should().Be(MigrationObservationState.PendingMigration);
        inspection.RequiresInitialBaseline.Should().BeTrue();
    }

    [Fact]
    public async Task QuestionIdOnlyReproduction_ClassifiesAsInspectionFailed_WithoutAnyWrite()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.CreateQuestionIdOnlyAsync(context);
        var before = await MigrationGoldenStates.CaptureStateAsync(context);

        await AssertState(context, MigrationObservationState.InspectionFailed);

        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task PartialTagTables_ClassifyAsInspectionFailed()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, "DROP TABLE question_tag");
        var before = await MigrationGoldenStates.CaptureStateAsync(context);

        await AssertState(context, MigrationObservationState.InspectionFailed);

        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Theory]
    // history claims current but the schema contradicts it
    [InlineData("ALTER TABLE question DROP COLUMN width")]
    [InlineData("ALTER TABLE question ADD COLUMN unexpected text NULL")]
    [InlineData("ALTER TABLE question ALTER COLUMN level DROP NOT NULL")]
    [InlineData("ALTER TABLE question ALTER COLUMN level TYPE bigint")]
    [InlineData("DROP INDEX \"IX_question_created_at\"")]
    [InlineData("CREATE INDEX \"IX_question_unexpected\" ON question (grade)")]
    [InlineData("ALTER TABLE question_content DROP CONSTRAINT \"FK_question_content_question_question_id\"")]
    [InlineData("ALTER TABLE question DROP CONSTRAINT \"PK_question\" CASCADE")]
    [InlineData("DROP TABLE question_tag; DROP TABLE tag")]
    public async Task CorruptedCurrentDatabase_ClassifiesAsInspectionFailed_WithoutAnyWrite(string corruption)
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, corruption);
        var before = await MigrationGoldenStates.CaptureStateAsync(context);

        await AssertState(context, MigrationObservationState.InspectionFailed);

        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task ConstraintNameCaseDifferences_StillClassifyAsCurrent_WhenSemanticsMatch()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, """
            ALTER TABLE question RENAME CONSTRAINT "PK_question" TO pk_question;
            ALTER INDEX "IX_question_created_at" RENAME TO ix_question_created_at;
            ALTER TABLE question_content
                RENAME CONSTRAINT "FK_question_content_question_question_id"
                TO fk_question_content_question_question_id;
            """);

        await AssertState(context, MigrationObservationState.CurrentVersionCompatible);
    }

    [Fact]
    public async Task UnknownMigrationId_ClassifiesAsVersionTooNew_WithoutAnyWrite()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, $"""
            INSERT INTO "{MigrationGoldenStates.HistoryTable}" ("MigrationId", "ProductVersion")
            VALUES ('99999999999999_Future', '99.0.0')
            """);
        var before = await MigrationGoldenStates.CaptureStateAsync(context);

        var inspection = await InspectDetailed(context);

        inspection.State.Should().Be(MigrationObservationState.VersionTooNew);
        inspection.Reason.Should().Contain("99999999999999_Future");
        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task HistoryWithoutOrderedPrefix_ClassifiesAsInspectionFailed()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, $"""
            DELETE FROM "{MigrationGoldenStates.HistoryTable}"
            WHERE "MigrationId" = '{MigrationGoldenStates.InitialId}'
            """);

        await AssertState(context, MigrationObservationState.InspectionFailed);
    }

    [Fact]
    public async Task AuthenticationFailure_ClassifiesAsInspectionFailed_WithoutThrowing()
    {
        using var context = _fixture.CreateContextWithConnectionString(
            _fixture.GetInvalidPasswordConnectionString(_database));

        await AssertState(context, MigrationObservationState.InspectionFailed);
    }

    [Fact]
    public async Task ExtraNonBusinessTables_DoNotChangeClassification()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, """
            CREATE TABLE unrelated_service_state (id integer PRIMARY KEY, note text NULL)
            """);

        await AssertState(context, MigrationObservationState.CurrentVersionCompatible);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceled_BeforeAnyRead()
    {
        using var context = _fixture.CreateContext(_database);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => new QuaesturaMigrationExecutor(context)
            .InspectAsync(cancelled.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Task<MigrationObservationState> Inspect(QuaesturaDbContext context) =>
        new QuaesturaMigrationExecutor(context, NullLogger.Instance)
            .InspectAsync(CancellationToken.None).AsTask();

    /// <summary>Asserts the classification while surfacing the safe inspection reason on failure.</summary>
    private static async Task AssertState(
        QuaesturaDbContext context, MigrationObservationState expected)
    {
        var inspection = await InspectDetailed(context);
        inspection.State.Should().Be(expected, "inspection reason: {0}", inspection.Reason);
    }

    private static Task<MigrationInspection> InspectDetailed(QuaesturaDbContext context) =>
        new QuaesturaMigrationExecutor(context, NullLogger.Instance)
            .InspectDetailedAsync(CancellationToken.None);
}
