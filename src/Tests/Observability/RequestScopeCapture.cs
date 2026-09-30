using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Quaestura.Tests.Observability;

/// <summary>
/// Records the ILogger scopes opened during a request. The ServiceMantle request scope is an
/// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> of named fields; replacing the
/// logger factory of one derived factory with a plain one captures that scope state directly —
/// the same scope surface the shared ServiceMantle logging pipeline consumes. Actual Console/Loki
/// delivery belongs to the shared pipeline and is verified in ServiceMantleLoggingTests.
/// </summary>
public sealed class RequestScopeCapture : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<IReadOnlyList<KeyValuePair<string, object?>>> _scopes = [];

    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Scopes
    {
        get
        {
            lock (_gate)
            {
                return _scopes.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public void Dispose()
    {
    }

    private void Add(IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        lock (_gate)
        {
            _scopes.Add(fields);
        }
    }

    private sealed class CaptureLogger(RequestScopeCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                owner.Add(fields);
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Shared helpers for the ServiceMantle observability tests.
/// </summary>
public static class ObservabilityTestHelpers
{
    public const string CorrelationHeaderName = "x-correlation-id";

    /// <summary>
    /// Replaces the shared-pipeline-backed logger factory of one derived factory with a plain
    /// factory that records structured scope state, so request scopes can be asserted directly.
    /// </summary>
    public static void CaptureRequestScopes(IServiceCollection services, RequestScopeCapture capture)
    {
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(
            _ => LoggerFactory.Create(logging => logging.AddProvider(capture)));
    }

    /// <summary>
    /// Reads the named field out of one captured scope state.
    /// </summary>
    public static string? Field(
        IReadOnlyList<KeyValuePair<string, object?>> fields,
        string name)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, name, StringComparison.Ordinal))
            {
                return field.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Mirrors the library's entry-assembly version resolution (AddServiceMantle is called
    /// without an explicit serviceVersion): informational version, then assembly version, then
    /// "unknown". Under WebApplicationFactory the entry assembly is the test host, so the
    /// assertion proves the resolution rule rather than a hard-coded version string.
    /// </summary>
    public static string ExpectedEntryAssemblyServiceVersion()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var informationalVersion = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var assemblyVersion = entryAssembly?.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion;
    }
}
