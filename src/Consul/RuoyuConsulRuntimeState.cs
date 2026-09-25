namespace Quaestura.Consul;

public sealed class RuoyuConsulRuntimeState
{
    private readonly object _sync = new();

    public static RuoyuConsulRuntimeState Instance { get; } = new();

    public bool Enabled { get; private set; }

    public string Source { get; private set; } = "AppSettings";

    public string? LastError { get; private set; }

    public int KeyCount { get; private set; }

    public DateTimeOffset? LastLoadedAt { get; private set; }

    public DateTimeOffset? LastSuccessfulLoadAt { get; private set; }

    public IReadOnlyList<string> LoadedPrefixes { get; private set; } = Array.Empty<string>();

    public string CacheDirectory { get; private set; } = string.Empty;

    private RuoyuConsulRuntimeState()
    {
    }

    public void MarkLoaded(string source, int keyCount, IReadOnlyList<string> prefixes, string cacheDirectory)
    {
        lock (_sync)
        {
            Enabled = true;
            Source = source;
            LastError = null;
            KeyCount = keyCount;
            LastLoadedAt = DateTimeOffset.UtcNow;
            LastSuccessfulLoadAt = LastLoadedAt;
            LoadedPrefixes = prefixes;
            CacheDirectory = cacheDirectory;
        }
    }

    public void MarkFallback(string source, string? error, int keyCount, IReadOnlyList<string> prefixes, string cacheDirectory)
    {
        lock (_sync)
        {
            Enabled = true;
            Source = source;
            LastError = error;
            KeyCount = keyCount;
            LastLoadedAt = DateTimeOffset.UtcNow;
            LoadedPrefixes = prefixes;
            CacheDirectory = cacheDirectory;
        }
    }
}
