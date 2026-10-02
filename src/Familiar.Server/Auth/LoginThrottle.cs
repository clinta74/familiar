using System.Collections.Concurrent;

namespace Familiar.Server.Auth;

/// <summary>
/// Per-username exponential backoff after repeated failed logins, on top of the per-address rate
/// limit. Slows a guesser down without letting anyone lock someone else out for long.
/// </summary>
/// <remarks>
/// Keyed on the username as typed, whether or not the account exists - throttling only real names
/// would tell a caller which names are real. In memory: one server instance, and a restart clearing
/// the state is acceptable.
/// </remarks>
public sealed class LoginThrottle(AuthOptions options, TimeProvider clock)
{
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan Base => TimeSpan.FromSeconds(Math.Max(1, options.LoginBackoffSeconds));

    private TimeSpan Ceiling => TimeSpan.FromSeconds(Math.Max(options.LoginBackoffSeconds, options.LoginBackoffMaxSeconds));

    public TimeSpan? RetryAfter(string username)
    {
        if (options.LoginFailuresBeforeBackoff <= 0 || !_entries.TryGetValue(username, out var entry))
        {
            return null;
        }

        lock (entry)
        {
            var remaining = entry.LockedUntil - clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    /// <returns>True when this failure started a pause.</returns>
    public bool RecordFailure(string username)
    {
        if (options.LoginFailuresBeforeBackoff <= 0)
        {
            return false;
        }

        if (_entries.Count > PruneThreshold)
        {
            Prune();
        }

        var now = clock.GetUtcNow();
        var entry = _entries.GetOrAdd(username, _ => new Entry());

        lock (entry)
        {
            // Failures spread out over a long time are not a guessing attack.
            if (now - entry.LastFailure > Ceiling)
            {
                entry.Failures = 0;
            }

            entry.Failures++;
            entry.LastFailure = now;

            var over = entry.Failures - options.LoginFailuresBeforeBackoff;
            if (over < 0)
            {
                return false;
            }

            var pause = TimeSpan.FromTicks(Math.Min(Ceiling.Ticks, Base.Ticks * (1L << Math.Min(over, 30))));
            entry.LockedUntil = now + pause;
            return over == 0;
        }
    }

    public void RecordSuccess(string username) => _entries.TryRemove(username, out _);

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Ceiling;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastFailure < cutoff)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public int Failures { get; set; }

        public DateTimeOffset LastFailure { get; set; }

        public DateTimeOffset LockedUntil { get; set; }
    }
}
