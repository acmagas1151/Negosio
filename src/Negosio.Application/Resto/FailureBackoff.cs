using System.Collections.Concurrent;

namespace Negosio.Application.Resto;

/// <summary>
/// In-memory exponential backoff for repeatedly failing keys (a tenant, or one order). Delays are 1, 2, 4 … minutes,
/// capped at the configured maximum. State is per process and resets on restart; that is accepted for M3 and every
/// failure is logged, so the history survives in logs.
/// </summary>
public sealed class FailureBackoff<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    private sealed record Entry(int Failures, DateTime RetryAfterUtc);

    public bool IsBackedOff(TKey key, DateTime nowUtc) =>
        _entries.TryGetValue(key, out var entry) && nowUtc < entry.RetryAfterUtc;

    /// <summary>Records a failure and returns the time before which the key will be skipped.</summary>
    public DateTime RecordFailure(TKey key, DateTime nowUtc, int maxMinutes)
    {
        var updated = _entries.AddOrUpdate(
            key,
            _ => new Entry(1, nowUtc.AddMinutes(1)),
            (_, previous) =>
            {
                var failures = previous.Failures + 1;
                var minutes = Math.Min(Math.Pow(2, failures - 1), maxMinutes);
                return new Entry(failures, nowUtc.AddMinutes(minutes));
            });
        return updated.RetryAfterUtc;
    }

    public void Clear(TKey key) => _entries.TryRemove(key, out _);

    /// <summary>Keys currently in backoff at <paramref name="nowUtc"/>.</summary>
    public IReadOnlyList<TKey> ActiveKeys(DateTime nowUtc) =>
        _entries.Where(e => nowUtc < e.Value.RetryAfterUtc).Select(e => e.Key).ToList();
}
