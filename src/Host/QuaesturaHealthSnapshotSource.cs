using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quaestura.Database;
using ServiceMantle.Health;
using ServiceMantle.Installation;

namespace Quaestura.Host;

/// <summary>
/// The single consumer-owned <see cref="IServiceHealthSnapshotSource"/> of this service. It is
/// registered scoped, so every readiness request evaluates through its own scope and its own
/// <see cref="QuaesturaDbContext"/>; concurrent requests never share a context and no stale
/// readiness is cached. The snapshot is strictly read-only: one cancellable minimal business
/// table query as the per-request database evidence, plus the in-process startup observation.
/// It never runs database creation or migrations, never writes history, never resolves the
/// Bootstrap file store or any installation storage, and never returns driver details: failures
/// collapse into the fixed safe <see cref="DatabaseUnreachableErrorCode"/>.
/// </summary>
public sealed class QuaesturaHealthSnapshotSource(
    QuaesturaDbContext dbContext,
    QuaesturaStartupHealthState startupState,
    ILogger<QuaesturaHealthSnapshotSource>? logger = null) : IServiceHealthSnapshotSource
{
    /// <summary>The fixed safe error code reported when the per-request probe fails.</summary>
    public const string DatabaseUnreachableErrorCode = "health.database_unreachable";

    /// <inheritdoc/>
    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        // Caller cancellation and the library's probe budget both arrive on this token; both
        // must propagate unchanged so the endpoint surfaces the original cancellation and the
        // library classifies its own timeout.
        cancellationToken.ThrowIfCancellationRequested();

        var migrationStatus = startupState.MigrationStatus;
        // External-configuration model: "completed" claims only that this preconfigured host
        // passed its startup gate, not that any installation record or interactive setup
        // exists. Before the gate passes, the default enum value is never dressed up as
        // success.
        var phase = startupState.StartupGatePassed
            ? ServiceStartupPhase.Completed
            : ServiceStartupPhase.BootstrapConfiguration;

        ServiceDatabaseReadinessState databaseStatus;
        string? errorCode = null;
        try
        {
            // The per-request evidence: a connection plus a minimal read-only business table
            // query (SELECT EXISTS over "tag"), tracked nowhere, writing nothing.
            await dbContext.Tags.AsNoTracking().AnyAsync(cancellationToken)
                .ConfigureAwait(false);
            databaseStatus = ServiceDatabaseReadinessState.Reachable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fixed safe classification only: the exception type name goes to the local log,
            // never into the probe response.
            logger?.LogWarning(
                "Health database probe failed: {ExceptionType}", exception.GetType().Name);
            databaseStatus = ServiceDatabaseReadinessState.Unreachable;
            errorCode = DatabaseUnreachableErrorCode;
        }

        return new ServiceHealthSnapshot(phase, migrationStatus, databaseStatus, errorCode);
    }
}
