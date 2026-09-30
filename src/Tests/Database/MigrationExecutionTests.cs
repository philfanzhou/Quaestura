using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quaestura.Database;
using Xunit;

namespace Quaestura.Tests.Database;

/// <summary>
/// Execution and takeover behavior against real PostgreSQL under the shared migration
/// orchestrator (real advisory lock): the empty state migrates to the six tables with both
/// original history rows; verified legacy databases are taken over by registering only the
/// verified InitialCreate baseline and really executing AddTags while business data (including
/// tag usage counts) is preserved; cancellation between phases keeps only the real committed
/// baseline and recovers on the next orchestration; the second orchestration over a current
/// database executes nothing; unknown or corrupt states are refused with stable error codes and
/// zero writes; and secret canaries never enter the orchestration's error surfaces.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class MigrationExecutionTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public MigrationExecutionTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _database = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task EmptyDatabase_InitializesToSixTablesAndBothHistoryRows()
    {
        using var context = _fixture.CreateContext(_database);

        var result = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));

        result.Succeeded.Should().BeTrue();
        result.ExecutorWasCalled.Should().BeTrue();
        await AssertCurrentContractAsync(context);
    }

    [Fact]
    public async Task MissingCatalog_IsCreatedByMigration_AndVerifiesCurrent()
    {
        // Executor-level contract: the missing-catalog observation classifies as Empty and EF
        // Core Migrate creates the target. In production the shared target preparation (#41)
        // creates the database before the migration lock is acquired, so orchestration always
        // sees an existing catalog; this executor path is the same one the orchestrator calls
        // under the lock.
        var database = $"missing_{Guid.NewGuid():N}";
        try
        {
            using var context = _fixture.CreateContext(database);
            var executor = new QuaesturaMigrationExecutor(context, NullLogger.Instance);

            (await executor.InspectAsync().AsTask())
                .Should().Be(ServiceMantle.Migration.MigrationObservationState.Empty);
            await executor.ExecuteAsync();

            await AssertCurrentContractAsync(context);
        }
        finally
        {
            await _fixture.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task LegacyWithoutHistory_IsTakenOver_WithOnlyTheVerifiedBaselineAndDataPreserved()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyInitialOnlyAsync(context);
        await MigrationGoldenStates.DropHistoryAsync(context);
        await MigrationGoldenStates.SeedInitialEraRowsAsync(context);

        var result = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));

        result.Succeeded.Should().BeTrue();
        result.ExecutorWasCalled.Should().BeTrue();
        // Only the verified InitialCreate baseline is registered by the executor; AddTags really
        // executed (it created the tag tables) and EF recorded it.
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(
            MigrationGoldenStates.InitialId, MigrationGoldenStates.AddTagsId);
        await AssertCurrentContractAsync(context);
        await AssertInitialEraRowsPreservedAsync(context);
    }

    [Fact]
    public async Task EnsureCreatedLegacy_WithTagTables_PreservesUsageCountsAndRows()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);
        await MigrationGoldenStates.SeedInitialEraRowsAsync(context);
        await MigrationGoldenStates.SeedTagEraRowsAsync(context);

        var result = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));

        result.Succeeded.Should().BeTrue();
        result.ExecutorWasCalled.Should().BeTrue();
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(
            MigrationGoldenStates.InitialId, MigrationGoldenStates.AddTagsId);
        await AssertCurrentContractAsync(context);
        await AssertInitialEraRowsPreservedAsync(context);
        (await MigrationGoldenStates.ScalarAsync<int>(
            context,
            $"SELECT usage_count FROM tag WHERE id = '{MigrationGoldenStates.TagId}'"))
            .Should().Be(MigrationGoldenStates.LegacyUsageCount);
        (await MigrationGoldenStates.CountRowsAsync(context, "question_tag")).Should().Be(1);
    }

    [Fact]
    public async Task CancellationAfterBaselineCommit_KeepsOnlyTheRealBaseline_AndRecoversNextRun()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyInitialOnlyAsync(context);
        await MigrationGoldenStates.DropHistoryAsync(context);
        await MigrationGoldenStates.SeedInitialEraRowsAsync(context);

        using var cts = new CancellationTokenSource();
        var executor = new QuaesturaMigrationExecutor(
            context,
            NullLogger.Instance,
            afterInitialBaselineCommitted: token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await FluentActions.Awaiting(() => executor.ExecuteAsync(cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        // Only the real committed baseline survives: no fabricated AddTags history and no tag
        // tables, because the remaining phase never started.
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context))
            .Should().Equal(MigrationGoldenStates.InitialId);
        (await MigrationGoldenStates.TableExistsAsync(context, "tag")).Should().BeFalse();
        (await MigrationGoldenStates.TableExistsAsync(context, "question_tag")).Should().BeFalse();

        // The next run re-verifies the legitimate pending state and completes the recovery.
        using var recovery = new CancellationTokenSource();
        var recovered = await MigrationOrchestration.OrchestrateAsync(
            context,
            _fixture.GetConnectionString(_database),
            cancellationToken: recovery.Token);

        recovered.Succeeded.Should().BeTrue();
        recovered.ExecutorWasCalled.Should().BeTrue();
        await AssertCurrentContractAsync(context);
        await AssertInitialEraRowsPreservedAsync(context);
    }

    [Fact]
    public async Task PreCancelledToken_ExecutesNothing()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyInitialOnlyAsync(context);
        await MigrationGoldenStates.DropHistoryAsync(context);
        var before = await MigrationGoldenStates.CaptureStateAsync(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var executor = new QuaesturaMigrationExecutor(context, NullLogger.Instance);
        await FluentActions.Awaiting(() => executor.ExecuteAsync(cancelled.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task OrchestrationIsIdempotent_SecondRunSkipsExecutionWithZeroChanges()
    {
        using var context = _fixture.CreateContext(_database);

        var first = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));
        first.Succeeded.Should().BeTrue();
        first.ExecutorWasCalled.Should().BeTrue();
        var firstState = await MigrationGoldenStates.CaptureStateAsync(context);

        // The second instance re-reads the current state under the lock and executes nothing.
        var second = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));
        second.Succeeded.Should().BeTrue();
        second.ExecutorWasCalled.Should().BeFalse();

        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(firstState);
        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(
            MigrationGoldenStates.InitialId, MigrationGoldenStates.AddTagsId);
    }

    [Fact]
    public async Task QuestionIdOnlyReproduction_IsRefusedByOrchestration_WithoutAnyWrite()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.CreateQuestionIdOnlyAsync(context);
        var before = await MigrationGoldenStates.CaptureStateAsync(context);

        var result = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(
            ServiceMantle.Migration.WellKnownMigrationErrorCodes.InspectionFailed);
        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task UnknownHistory_IsRefusedByOrchestration_WithoutAnyWrite()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
        await MigrationGoldenStates.ExecuteRawAsync(context, $"""
            INSERT INTO "{MigrationGoldenStates.HistoryTable}" ("MigrationId", "ProductVersion")
            VALUES ('99999999999999_Future', '99.0.0')
            """);
        var before = await MigrationGoldenStates.CaptureStateAsync(context);

        var result = await MigrationOrchestration.OrchestrateAsync(
            context, _fixture.GetConnectionString(_database));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(
            ServiceMantle.Migration.WellKnownMigrationErrorCodes.VersionTooNew);
        (await MigrationGoldenStates.CaptureStateAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task DuplicateTakeover_RunsNeverDoubleStampTheBaseline()
    {
        using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.EnsureCreatedLegacyAsync(context);

        // Two sequential executor runs: the first takes over, the second re-reads the legitimate
        // state and must not add or duplicate history rows.
        await new QuaesturaMigrationExecutor(context, NullLogger.Instance)
            .ExecuteAsync(CancellationToken.None).AsTask();
        await new QuaesturaMigrationExecutor(context, NullLogger.Instance)
            .ExecuteAsync(CancellationToken.None).AsTask();

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(
            MigrationGoldenStates.InitialId, MigrationGoldenStates.AddTagsId);
    }

    [Fact]
    public async Task SecretCanary_NeverEntersOrchestrationErrorSurfaces()
    {
        var invalid = _fixture.GetInvalidPasswordConnectionString(_database);
        using var context = _fixture.CreateContextWithConnectionString(invalid);

        var result = await MigrationOrchestration.OrchestrateAsync(context, invalid);

        // Authentication failures refuse to start with the stable lock classification only:
        // the result surfaces never carry the canary, the connection string, SQL, or any
        // driver detail.
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(
            ServiceMantle.Migration.WellKnownMigrationErrorCodes.LockFailed);
        result.ErrorMessage.Should().NotContain(_fixture.PasswordCanary);
        result.ErrorMessage.Should().NotContain(invalid);
        result.ErrorMessage.Should().NotContain(_fixture.GetConnectionString(_database));
        result.ToString().Should().NotContain(_fixture.PasswordCanary);
    }

    private static async Task AssertCurrentContractAsync(QuaesturaDbContext context)
    {
        foreach (var table in QuaesturaMigrationExecutor.KnownTableNames)
        {
            (await MigrationGoldenStates.TableExistsAsync(context, table))
                .Should().BeTrue($"table '{table}' must exist after initialization");
        }

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(
            MigrationGoldenStates.InitialId, MigrationGoldenStates.AddTagsId);

        // The final inspection of the initializer already verified Current; re-check explicitly.
        var state = await new QuaesturaMigrationExecutor(context, NullLogger.Instance)
            .InspectAsync(CancellationToken.None).AsTask();
        state.Should().Be(ServiceMantle.Migration.MigrationObservationState.CurrentVersionCompatible);
    }

    private static async Task AssertInitialEraRowsPreservedAsync(QuaesturaDbContext context)
    {
        (await MigrationGoldenStates.CountRowsAsync(context, "question")).Should().Be(1);
        (await MigrationGoldenStates.ScalarAsync<string>(
            context,
            $"SELECT content FROM question_content WHERE question_id = '{MigrationGoldenStates.QuestionId}'"))
            .Should().Be("legacy question content");
        (await MigrationGoldenStates.CountRowsAsync(context, "knowledge")).Should().Be(1);
        (await MigrationGoldenStates.ScalarAsync<double>(
            context,
            $"SELECT weight FROM question_knowledge WHERE id = '{MigrationGoldenStates.QuestionKnowledgeId}'"))
            .Should().Be(0.75);
    }
}
