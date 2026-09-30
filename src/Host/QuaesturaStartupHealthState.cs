using System.Threading;
using ServiceMantle.Health;

namespace Quaestura.Host;

/// <summary>
/// Thread-safe, in-process observation of the host's real database initialization outcome.
/// Program.cs records the transitions while the startup gate runs; the health snapshot source
/// reads them per request. Nothing is persisted and nothing is inferred from installation
/// storage: <see cref="ServiceMigrationReadinessState.Succeeded"/> is recorded only after the
/// actual initialization (the relational two-stage prepare + strict initializer path, or the
/// non-relational Testing path) completed successfully. The default state is
/// <see cref="ServiceMigrationReadinessState.NotStarted"/>, which readiness evaluation never
/// treats as success. A failed initialization means the host never listens in production; the
/// recorded <see cref="ServiceMigrationReadinessState.Failed"/> state exists so injected test
/// scenarios observe the honest value.
/// </summary>
public sealed class QuaesturaStartupHealthState
{
    private int _migrationStatus = (int)ServiceMigrationReadinessState.NotStarted;

    /// <summary>Gets the current initialization state without any I/O.</summary>
    public ServiceMigrationReadinessState MigrationStatus =>
        (ServiceMigrationReadinessState)Volatile.Read(ref _migrationStatus);

    /// <summary>
    /// Gets whether the externally configured host passed its startup gate. This is the only
    /// condition under which the health snapshot reports
    /// <see cref="ServiceMantle.Installation.ServiceStartupPhase.Completed"/>; it claims no
    /// ServiceMantle installation record and no interactive setup.
    /// </summary>
    public bool StartupGatePassed =>
        MigrationStatus == ServiceMigrationReadinessState.Succeeded;

    /// <summary>Marks the initialization as running; a terminal state is never downgraded.</summary>
    public void RecordRunning() =>
        TransitionTo(ServiceMigrationReadinessState.Running, terminalOnly: false);

    /// <summary>Records the real initialization success; terminal states are final.</summary>
    public void RecordSucceeded() =>
        TransitionTo(ServiceMigrationReadinessState.Succeeded, terminalOnly: true);

    /// <summary>Records an initialization failure; terminal states are final.</summary>
    public void RecordFailed() =>
        TransitionTo(ServiceMigrationReadinessState.Failed, terminalOnly: true);

    private void TransitionTo(ServiceMigrationReadinessState target, bool terminalOnly)
    {
        var current = Volatile.Read(ref _migrationStatus);
        while (true)
        {
            var currentIsTerminal =
                current == (int)ServiceMigrationReadinessState.Succeeded ||
                current == (int)ServiceMigrationReadinessState.Failed;
            if (currentIsTerminal && (terminalOnly || current != (int)target))
            {
                return;
            }

            var previous = Interlocked.CompareExchange(
                ref _migrationStatus, (int)target, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
