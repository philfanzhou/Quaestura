using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Quaestura.Consul;

/// <summary>
/// Shared Serilog + Grafana Loki logging extensions for all services.
/// Identity keeps its own local copy and does not reference this class.
/// </summary>
public static class RuoyuSerilogExtensions
{
    /// <summary>
    /// Inject Loki:Uri (typically from Consul KV) into Serilog sink configuration,
    /// overriding the fallback uri in appsettings.json.
    /// Must be called before <see cref="UseRuoyuSerilog"/>.
    /// </summary>
    /// <remarks>
    /// Loki Sink throws ArgumentNullException when uri is null; the fallback uri in
    /// appsettings.json ensures startup. When Loki is unreachable, the sink retries
    /// asynchronously and does not affect service operation.
    /// </remarks>
    public static ConfigurationManager AddRuoyuLokiSink(this ConfigurationManager configuration)
    {
        var lokiUri = configuration["Loki:Uri"];
        if (!string.IsNullOrWhiteSpace(lokiUri))
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:1:Args:uri"] = lokiUri
            });
        }
        return configuration;
    }

    /// <summary>
    /// Configure Serilog with Console + Grafana Loki sinks, enriched with service identity.
    /// </summary>
    /// <param name="serviceName">Service name used as Loki label for Grafana filtering.</param>
    /// <param name="serviceVersion">Service version, defaults to 1.0.0.</param>
    public static IHostBuilder UseRuoyuSerilog(
        this IHostBuilder hostBuilder,
        string serviceName,
        string serviceVersion = "1.0.0")
    {
        return hostBuilder.UseSerilog((context, services, loggerConfiguration) =>
        {
            loggerConfiguration
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("ServiceName", serviceName)
                .Enrich.WithProperty("ServiceVersion", serviceVersion)
                .Enrich.WithProperty("InstanceId", Environment.MachineName)
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services);
        });
    }
}
