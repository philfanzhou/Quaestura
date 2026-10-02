using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quaestura.Database;
using Quaestura.Host;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;

namespace Quaestura.Tests.Database;

/// <summary>Exercises the production registration and direct gate, including the real lease.</summary>
internal sealed class StartupGateHarness(IDatabaseMigrationExecutor? executor = null, IDatabaseTargetPreparationProvider? preparation = null)
{
    internal StartupDatabaseReceipt Receipt { get; } = new();

    internal async ValueTask<StartupDatabaseGateResult> OrchestrateMigrationAsync(
        ServiceId serviceId, BootstrapDatabaseConfiguration target, TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<QuaesturaDbContext>(options => options.UseNpgsql(target.ConnectionString));
        services.AddQuaesturaServiceMantle();
        if (executor is not null)
        {
            services.RemoveAll<IDatabaseMigrationExecutor>();
            services.AddSingleton(executor);
        }
        if (preparation is not null)
        {
            services.RemoveAll<IDatabaseTargetPreparationProvider>();
            services.AddSingleton(preparation);
        }
        await using var provider = services.BuildServiceProvider();
        var options = QuaesturaStartupDatabaseOptions.Create(new ConfigurationBuilder().Build(), target.ConnectionString);
        options = new StartupDatabaseGateOptions(options.Database, DatabaseDeploymentMode.MultiInstance, budget,
            true, options.AllowTargetCreation, options.MaintenanceConnectionString, options.PreparationTimeout);
        try
        {
            var result = await provider.GetRequiredService<StartupDatabaseGate>()
                .RunAsync(options, Receipt, serviceId, cancellationToken);
            Receipt.State.Should().Be(result.Succeeded
                ? ServiceMigrationReadinessState.Succeeded : ServiceMigrationReadinessState.Failed);
            if (!result.Succeeded) Receipt.ErrorCode.Should().Be(result.ErrorCode);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            exception.CancellationToken.Should().Be(cancellationToken);
            Receipt.State.Should().Be(ServiceMigrationReadinessState.Running);
            Receipt.ErrorCode.Should().BeNull();
            throw;
        }
    }
}
