using System.Collections.Concurrent;

namespace Negosio.Application.Resto;

/// <summary>
/// Process-wide resume point per tenant. A release pass that stops at its page budget leaves its cursor here, so the next
/// poll continues after the last row it finished instead of restarting at the head of the queue. Without this, a head full
/// of permanently refused orders would hide every later order from every cycle. Resets on restart, which only means the
/// next pass starts from the beginning again.
/// </summary>
public sealed class PayoReleaseCursorStore
{
    private readonly ConcurrentDictionary<Guid, (DateTime SettledAtUtc, Guid OrderId)> _cursors = new();

    public (DateTime SettledAtUtc, Guid OrderId)? Get(Guid tenantId)
    {
        if (_cursors.TryGetValue(tenantId, out var cursor))
        {
            return cursor;
        }

        return null;
    }

    /// <summary>Stores the cursor, or clears it (null) once a full pass has reached the end of the queue.</summary>
    public void Set(Guid tenantId, (DateTime SettledAtUtc, Guid OrderId)? cursor)
    {
        if (cursor is { } value)
        {
            _cursors[tenantId] = value;
        }
        else
        {
            _cursors.TryRemove(tenantId, out _);
        }
    }
}
