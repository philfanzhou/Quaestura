using Microsoft.Extensions.Logging;
using Quaestura.Database;
using Quaestura.Host;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Migration;

namespace Quaestura.Tests.Database;

/// <summary>
/// Builds the production-shaped migration orchestration for the container-backed tests: the real
/// Quaestura executor, the real PostgreSQL advisory lock provider, and the shared orchestrator
/// bound to the production service id. There is deliberately no lock-free path here — every
/// orchestration in these tests acquires the real session advisory lock.
/// </summary>
internal static class MigrationOrchestration
{
    /// <summary>The same service id the host's Program.cs uses for orchestration.</summary>
    internal static readonly ServiceId ServiceId = ServiceMantle.ServiceId.Parse(
        ServiceMantleComposition.ServiceIdValue);

    /// <summary>The production lock acquire budget.</summary>
    internal static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(30);

    internal static BootstrapDatabaseConfiguration Target(string connectionString) =>
        new(WellKnownDatabaseProviderIds.PostgreSql, serverVersion: null, connectionString);

    /// <summary>Creates an orchestrator around an injected executor with the real lock provider.</summary>
    internal static DatabaseMigrationOrchestrator CreateOrchestrator(
        IDatabaseMigrationExecutor executor) =>
        new(
            executor,
            new DatabaseMigrationLockProviderRegistry(
                [new PostgreSqlMigrationLockProvider()],
                DatabaseProviderIdResolver.Empty));

    /// <summary>
    /// Runs one real orchestration: acquire the advisory lock, inspect, execute when required,
    /// re-inspect under the lock, and release.
    /// </summary>
    internal static Task<MigrationExecutionResult> OrchestrateAsync(
        QuaesturaDbContext context,
        string connectionString,
        TimeSpan? acquireTimeout = null,
        CancellationToken cancellationToken = default) =>
        CreateOrchestrator(new QuaesturaMigrationExecutor(context))
            .OrchestrateMigrationAsync(
                ServiceId,
                Target(connectionString),
                acquireTimeout ?? DefaultAcquireTimeout,
                cancellationToken)
            .AsTask();
}
