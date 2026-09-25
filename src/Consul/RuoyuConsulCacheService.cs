using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Quaestura.Consul;

public sealed class RuoyuConsulCacheService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    private readonly string _cacheFilePath;
    private readonly string _metadataFilePath;
    private readonly string _cacheDirectory;
    private readonly string _consulAddress;
    private readonly ILogger<RuoyuConsulCacheService>? _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public RuoyuConsulCacheService(string cacheDirectory, string consulAddress, ILogger<RuoyuConsulCacheService>? logger = null)
    {
        _cacheDirectory = cacheDirectory;
        _consulAddress = consulAddress;
        _cacheFilePath = Path.Combine(cacheDirectory, "cache.json");
        _metadataFilePath = Path.Combine(cacheDirectory, "cache.metadata.json");
        _logger = logger;
    }

    public Dictionary<string, string?>? Load()
    {
        _semaphore.Wait();
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_cacheFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogCritical(ex, "Consul cache file is corrupted: {FilePath}", _cacheFilePath);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Save(Dictionary<string, string?> data)
    {
        _semaphore.Wait();
        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            var tmpPath = _cacheFilePath + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(tmpPath, json);

            var verify = File.ReadAllText(tmpPath);
            _ = JsonSerializer.Deserialize<Dictionary<string, string?>>(verify, JsonOptions);

            if (File.Exists(_cacheFilePath))
            {
                File.Replace(tmpPath, _cacheFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmpPath, _cacheFilePath);
            }

            var metadata = new CacheMetadata
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                ConsulAddress = _consulAddress,
                KeyCount = data.Count
            };
            var metaJson = JsonSerializer.Serialize(metadata, JsonOptions);
            var metaTmp = _metadataFilePath + ".tmp";
            File.WriteAllText(metaTmp, metaJson);

            if (File.Exists(_metadataFilePath))
            {
                File.Replace(metaTmp, _metadataFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(metaTmp, _metadataFilePath);
            }

            _logger?.LogDebug("Consul cache saved: {Count} keys at {Path}", data.Count, _cacheFilePath);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _semaphore.Dispose();
        _disposed = true;
    }

    private sealed class CacheMetadata
    {
        public DateTimeOffset UpdatedAt { get; set; }

        public string ConsulAddress { get; set; } = string.Empty;

        public int KeyCount { get; set; }
    }
}
