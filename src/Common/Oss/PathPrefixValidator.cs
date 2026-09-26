using System;
using System.Linq;

namespace Quaestura.Common.Oss;

/// <summary>
/// Path prefix validator that ensures the service only operates on OSS objects under authorized path prefixes
/// </summary>
public class PathPrefixValidator
{
    private readonly string[] _allowedPrefixes;

    public PathPrefixValidator(string[] allowedPrefixes)
    {
        _allowedPrefixes = allowedPrefixes ?? Array.Empty<string>();
    }

    /// <summary>
    /// Validates that the path is within an allowed prefix; throws UnauthorizedAccessException otherwise
    /// </summary>
    public void ValidateWritePath(string path)
    {
        if (_allowedPrefixes.Length == 0)
            return; // Skip validation when nothing is configured

        if (string.IsNullOrWhiteSpace(path))
            throw new UnauthorizedAccessException($"Path is empty, allowed prefixes: {string.Join(", ", _allowedPrefixes)}");

        var normalizedPath = path.StartsWith("/") ? path[1..] : path;

        if (!_allowedPrefixes.Any(prefix => normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException($"Path '{path}' is not within allowed prefixes: {string.Join(", ", _allowedPrefixes)}");
        }
    }
}
