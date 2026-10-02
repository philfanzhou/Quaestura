using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using ServiceMantle.Bootstrap;

namespace Quaestura.Host;

/// <summary>Explicitly declares PostgreSQL deployment support; the shared gate owns coordination.</summary>
public sealed class QuaesturaDatabaseDeploymentCapability : IDatabaseDeploymentCapabilityProvider
{
    public DatabaseDeploymentCapability Capability { get; } = new(
        WellKnownDatabaseProviderIds.PostgreSql, DatabaseDeploymentSupport.SingleAndMultiInstance);

    public ValueTask<string> GetCanonicalTargetIdentityAsync(
        BootstrapDatabaseConfiguration target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
        // MultiInstance uses the advisory lock, not this identity. Credentials are excluded.
        return ValueTask.FromResult($"{(builder.Host ?? string.Empty).Trim().ToLowerInvariant()}:{builder.Port}/{builder.Database}");
    }
}
