using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quaestura.Host.Authentication;

/// <summary>
/// The server-side session store of the admin hosted-login slice: an in-process,
/// capacity-bounded LRU dictionary behind the cookie handler's <c>SessionStore</c> extension
/// point. Keys are 256-bit random values; the browser only ever holds the data-protected key,
/// so neither the principal nor the saved tokens leave the server.
/// </summary>
/// <remarks>
/// <para>
/// Capacity defaults to the replaced package's <c>TicketCapacity</c> bound and follows the
/// <c>AdminOidc:TicketCapacity</c> key. The deliberate trade-off, adopted from the reference
/// migrations: the package <b>refused</b> new tickets when full, never evicting a live session;
/// this store instead evicts the least-recently-used ticket so a full store degrades the oldest
/// idle session to a fixed re-authentication answer instead of locking out every new
/// administrator. Process restarts and multiple replicas lose or fail to share sessions — the
/// same documented limitation the package carried.
/// </para>
/// <para>
/// Expiry itself is enforced by the cookie handler from the ticket's <c>ExpiresUtc</c>; this
/// store additionally drops expired tickets on access and from the periodic sweep of
/// <see cref="AdminOidcTicketStoreCleanupService"/> so abandoned keys do not hold capacity.
/// </para>
/// </remarks>
public sealed class AdminOidcTicketStore(TimeProvider? timeProvider = null, int? capacity = null) : ITicketStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly int _capacity = capacity ?? 10_000;
    private readonly object _gate = new();
    private readonly LinkedList<string> _order = new(); // most-recently-used first
    private readonly Dictionary<string, (LinkedListNode<string> Node, AuthenticationTicket Ticket)> _tickets = new();

    /// <summary>The number of tickets currently held, including not-yet-swept expired ones.</summary>
    public int Count
    {
        get { lock (_gate) { return _tickets.Count; } }
    }

    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        string key;
        lock (_gate)
        {
            EvictWhileFull();
            do
            {
                key = NewKey();
            }
            while (_tickets.ContainsKey(key));
            _tickets[key] = (_order.AddFirst(key), ticket);
        }

        return Task.FromResult(key);
    }

    /// <inheritdoc />
    /// <remarks>The LRU override is atomic under the store lock: one capacity check, then the
    /// ticket is replaced and its recency refreshed in one swap — the fresh sign-in replaces the
    /// session this browser held before, exactly like the package's replacement call did. An
    /// unknown key (evicted or never issued) is not an error: the renewal degrades to a plain
    /// store under a new key.</remarks>
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(ticket);
        lock (_gate)
        {
            if (!_tickets.TryGetValue(key, out var entry))
            {
                EvictWhileFull();
                var newKey = NewKey();
                _tickets[newKey] = (_order.AddFirst(newKey), ticket);
                return Task.CompletedTask;
            }

            _order.Remove(entry.Node);
            _tickets[key] = (_order.AddFirst(key), ticket);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Retrieval refreshes the ticket's recency, and an expired ticket is reclaimed on
    /// the spot instead of being handed back.</remarks>
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Task.FromResult<AuthenticationTicket?>(null);
        }

        AuthenticationTicket? ticket = null;
        lock (_gate)
        {
            if (_tickets.TryGetValue(key, out var entry))
            {
                var expiresUtc = entry.Ticket.Properties.ExpiresUtc;
                if (expiresUtc is { } expiry && expiry <= _time.GetUtcNow())
                {
                    _order.Remove(entry.Node);
                    _tickets.Remove(key);
                }
                else
                {
                    _order.Remove(entry.Node);
                    _tickets[key] = (_order.AddFirst(key), entry.Ticket);
                    ticket = entry.Ticket;
                }
            }
        }

        return Task.FromResult(ticket);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_tickets.Remove(key, out var entry))
            {
                _order.Remove(entry.Node);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops every ticket whose <c>ExpiresUtc</c> has passed and returns the number removed, so
    /// expired sessions are reclaimed even when no request ever presents their key again.
    /// </summary>
    internal int RemoveExpired()
    {
        var removed = 0;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (key, entry) in _tickets)
            {
                if (entry.Ticket.Properties.ExpiresUtc is { } expiry && expiry <= now
                    && _tickets.Remove(key))
                {
                    _order.Remove(entry.Node);
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>The periodic sweep cadence of
    /// <see cref="AdminOidcTicketStoreCleanupService"/>.</summary>
    internal static TimeSpan CleanupInterval => SweepInterval;

    /// <summary>A point-in-time copy of the held tickets (server-side only; test and diagnostic
    /// surface). The returned tickets are the live objects, not clones.</summary>
    internal AuthenticationTicket[] Snapshot()
    {
        lock (_gate)
        {
            return _tickets.Values.Select(entry => entry.Ticket).ToArray();
        }
    }

    private static string NewKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes);
    }

    private void EvictWhileFull()
    {
        while (_tickets.Count >= _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _tickets.Remove(oldest.Value);
        }
    }
}

/// <summary>
/// Periodically reclaims expired tickets of the in-process <see cref="AdminOidcTicketStore"/> so
/// sessions abandoned by their browser never hold capacity, mirroring the replaced package's
/// cleanup service under the same five-minute cadence.
/// </summary>
public sealed class AdminOidcTicketStoreCleanupService(
    AdminOidcTicketStore store,
    ILogger<AdminOidcTicketStoreCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AdminOidcTicketStore.CleanupInterval);
        do
        {
            try
            {
                var removed = store.RemoveExpired();
                if (removed > 0)
                {
                    logger.LogDebug("Removed {Count} expired admin session tickets", removed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep never stops the service; the next tick retries it.
                logger.LogWarning(exception, "Admin session ticket sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
