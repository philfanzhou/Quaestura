using System;
using System.Linq;

namespace Quaestura.Common.Oss;

/// <summary>
/// 路径前缀校验器，确保服务只能操作授权路径前缀下的 OSS 对象
/// </summary>
public class PathPrefixValidator
{
    private readonly string[] _allowedPrefixes;

    public PathPrefixValidator(string[] allowedPrefixes)
    {
        _allowedPrefixes = allowedPrefixes ?? Array.Empty<string>();
    }

    /// <summary>
    /// 校验路径是否在允许前缀内。如果不在，抛出 UnauthorizedAccessException
    /// </summary>
    public void ValidateWritePath(string path)
    {
        if (_allowedPrefixes.Length == 0)
            return; // 无配置时不校验

        if (string.IsNullOrWhiteSpace(path))
            throw new UnauthorizedAccessException($"Path is empty, allowed prefixes: {string.Join(", ", _allowedPrefixes)}");

        var normalizedPath = path.StartsWith("/") ? path[1..] : path;

        if (!_allowedPrefixes.Any(prefix => normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException($"Path '{path}' is not within allowed prefixes: {string.Join(", ", _allowedPrefixes)}");
        }
    }
}
