using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Quaestura.Database;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Migration;

namespace Quaestura.Host;

/// <summary>Non-relational test evidence only; production uses the shared relational source.</summary>
public sealed class TestingHealthSnapshotSource(
    QuaesturaDbContext dbContext, StartupDatabaseReceipt receipt) : IServiceHealthSnapshotSource
{
    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.State != ServiceMigrationReadinessState.Succeeded)
            return new ServiceHealthSnapshot(ServiceStartupPhase.PendingSetup, receipt.State,
                ServiceDatabaseReadinessState.Unreachable,
                receipt.State == ServiceMigrationReadinessState.Failed ? "health.startup_failed" : "health.startup_incomplete");
        await dbContext.Tags.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false);
        return new ServiceHealthSnapshot(ServiceStartupPhase.Completed, receipt.State,
            ServiceDatabaseReadinessState.Reachable);
    }
}
