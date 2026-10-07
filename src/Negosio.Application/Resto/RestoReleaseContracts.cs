namespace Negosio.Application.Resto;

/// <summary>A settled Pay-as-you-order round still in <c>Draft</c>: waiting to be released to the kitchen.</summary>
public sealed record PendingPayoReleaseRowDto(Guid OrderId, Guid RoundId, Guid BranchId, DateTime SettledAtUtc, long AgeSeconds);

/// <summary>
/// A kitchen ticket that is <b>available to the kitchen but unacknowledged</b>: the round was released and the item's
/// kitchen status is still <c>Pending</c>. This does not prove the kitchen received or displayed the ticket.
/// </summary>
public sealed record UnacknowledgedTicketRowDto(
    Guid ItemId,
    Guid OrderId,
    Guid RoundId,
    Guid BranchId,
    string StationName,
    string ProductName,
    decimal Quantity,
    DateTime ReleasedAtUtc,
    long AgeSeconds);

/// <summary>
/// One bounded page. Pass <see cref="NextAfterId"/> back as the matching <c>after*</c> parameter to continue; it is null
/// when there are no more rows.
/// </summary>
public sealed record RestoKeysetPageDto<T>(IReadOnlyList<T> Items, Guid? NextAfterId);

/// <summary>Outcome of one tenant's release pass. Failures are per order; a failing order never stops the pass.</summary>
public sealed record PayoReleaseCycleResult(
    int Scanned,
    int Released,
    int AlreadyReleased,
    int Refused,
    int Failed,
    IReadOnlyList<PayoReleaseFailure> Failures,
    int BackedOff);

public sealed record PayoReleaseFailure(Guid OrderId, Guid RoundId, string Error);
