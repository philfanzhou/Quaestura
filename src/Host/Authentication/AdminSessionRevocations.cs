using System;
using System.Collections.Concurrent;

namespace Quaestura.Host.Authentication;

/// <summary>
/// The recently revoked session keys of the hosted logout. Authentication results are cached
/// per request, so two concurrent logouts of one browser session can both observe the session
/// as live; the striped logout gates serialize them, and this marker — keyed by the presented
/// cookie value and set once per key under the gate — is the deterministic boundary that lets
/// exactly one of them construct the end-session redirect. Entries are held only as hashes and
/// reclaimed lazily after the five-minute logout window.
/// </summary>
internal sealed class AdminSessionRevocations(TimeProvider clock)
{
    private const int Capacity = 4096;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks one session key as revoked: returns <see langword="true"/> exactly once for a key
    /// this store has not seen before (the caller proceeds with the revocation), and
    /// <see langword="false"/> for a key already marked (the session is already gone).
    /// </summary>
    internal bool TryMark(string key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)));
        var now = clock.GetUtcNow();
        // Reclaim lazily: stale entries only ever make an already-revoked session answer
        // local-only, and the count stays bounded by the logouts of the last five minutes.
        foreach (var (existing, marked) in _revoked)
        {
            if (now - marked > Lifetime)
            {
                _revoked.TryRemove(existing, out _);
            }
        }

        return _revoked.TryAdd(hash, now);
    }
}
