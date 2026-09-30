using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ServiceMantle.Migration;

namespace Quaestura.Database;

/// <summary>
/// The business database startup entry: inspect the target strictly, execute migrations only for
/// the verified Empty/PendingMigration states, and require a verified current state at the end.
/// Unknown or corrupt databases are refused instead of being silently stamped or repaired.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Inspects and, when needed, migrates the target database of the given context.
    /// </summary>
    /// <param name="context">The EF Core context of the target database.</param>
    /// <param name="loggerFactory">The host logger factory.</param>
    /// <param name="cancellationToken">Observed by every database operation.</param>
    /// <exception cref="InvalidOperationException">
    /// The database is in an unknown, corrupt, or newer state, or the final inspection after
    /// execution did not verify the current version. No business-repair DDL is ever executed
    /// and nothing is stamped without schema verification.
    /// </exception>
    public static Task InitializeAsync(
        QuaesturaDbContext context,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default) =>
        InitializeAsync(context, loggerFactory, afterInitialBaselineCommitted: null, cancellationToken);

    /// <summary>
    /// Test seam variant that can observe (and cancel) the moment between the committed legacy
    /// baseline and the remaining EF Core migrations.
    /// </summary>
    internal static async Task InitializeAsync(
        QuaesturaDbContext context,
        ILoggerFactory loggerFactory,
        Func<CancellationToken, Task>? afterInitialBaselineCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("DatabaseInitializer");
        var executor = new QuaesturaMigrationExecutor(context, logger, afterInitialBaselineCommitted);

        var inspection = await executor.InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        switch (inspection.State)
        {
            case MigrationObservationState.CurrentVersionCompatible:
                logger.LogInformation(
                    "Database schema is compatible with the current version; no migration needed");
                return;

            case MigrationObservationState.Empty:
            case MigrationObservationState.PendingMigration:
                logger.LogInformation(
                    "Database inspection reported {State} ({Reason}); applying migrations",
                    inspection.State,
                    inspection.Reason);
                await executor.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                var final = await executor.InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
                if (final.State != MigrationObservationState.CurrentVersionCompatible)
                {
                    throw new InvalidOperationException(
                        $"Database migration finished but the final inspection reported {final.State}: " +
                        $"{final.Reason}. Refusing to continue startup.");
                }

                logger.LogInformation("Database migration completed and verified as current");
                return;

            case MigrationObservationState.VersionTooNew:
                throw new InvalidOperationException(
                    $"The database history is newer than this application supports: {inspection.Reason}. " +
                    "Refusing to start; no changes were made. Deploy an application version that knows this " +
                    "history, or restore a backup of a supported state.");

            default:
                throw new InvalidOperationException(
                    $"The database state could not be verified against any supported version: {inspection.Reason}. " +
                    "Refusing to start; no changes were made. Repair the database structure from a backup and " +
                    "restart; unknown schemas are never stamped or auto-repaired.");
        }
    }
}
