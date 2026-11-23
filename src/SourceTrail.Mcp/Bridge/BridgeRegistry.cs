using System.Collections.Concurrent;

namespace SourceTrail.Mcp.Bridge;

/// <summary>Tracks which stdio bridges are still connected to the shared HTTP server by their last heartbeat.</summary>
public sealed class BridgeRegistry(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _bridges = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Touch(string id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _bridges[id] = _clock.GetUtcNow();
    }

    public void Leave(string id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _bridges.TryRemove(id, out _);
    }

    /// <summary>Drops bridges whose heartbeat is older than the timeout and returns how many remain.</summary>
    public int ActiveCount(TimeSpan timeout)
    {
        var cutoff = _clock.GetUtcNow() - timeout;
        foreach (var (id, seen) in _bridges)
            if (seen < cutoff) _bridges.TryRemove(id, out _);
        return _bridges.Count;
    }
}

/// <summary>Decides when an auto-shutdown server has had no bridges for long enough.</summary>
public sealed class ShutdownClock(TimeSpan grace, DateTimeOffset startedAt)
{
    private DateTimeOffset? _emptySince = startedAt;

    /// <summary>Records the current bridge count; returns true when the server should exit.</summary>
    public bool Observe(int activeBridges, DateTimeOffset now)
    {
        if (activeBridges > 0) { _emptySince = null; return false; }
        _emptySince ??= now;
        return now - _emptySince >= grace;
    }
}

public sealed record BridgeBeat(string Id);
