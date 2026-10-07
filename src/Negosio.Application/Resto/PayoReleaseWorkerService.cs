using Negosio.Application.Abstractions;

namespace Negosio.Application.Resto;

public interface IPayoReleaseWorkerService
{
    /// <summary>
    /// Releases due Pay-as-you-order rounds for one tenant, resuming from where the previous pass stopped. Idempotent and
    /// safe to run from several instances at once.
    /// </summary>
    Task<PayoReleaseCycleResult> ReleaseDueRoundsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Released tickets still unacknowledged past the configured threshold, for logging.</summary>
    Task<int> CountUnacknowledgedAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class PayoReleaseWorkerService : IPayoReleaseWorkerService
{
    /// <summary>Pages examined per tenant per cycle. Bounds the work one tenant can take from the others in a single poll.</summary>
    public const int DefaultMaxPagesPerCycle = 10;

    private readonly ITenantDbContextFactory _factory;
    private readonly RestoReconciliationOptions _options;
    private readonly FailureBackoff<(Guid TenantId, Guid OrderId)> _orderBackoff;
    private readonly PayoReleaseCursorStore _cursors;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPagesPerCycle;

    public PayoReleaseWorkerService(
        ITenantDbContextFactory factory,
        RestoReconciliationOptions options,
        FailureBackoff<(Guid TenantId, Guid OrderId)> orderBackoff,
        PayoReleaseCursorStore cursors,
        TimeProvider timeProvider,
        int maxPagesPerCycle = DefaultMaxPagesPerCycle)
    {
        _factory = factory;
        _options = options;
        _orderBackoff = orderBackoff;
        _cursors = cursors;
        _timeProvider = timeProvider;
        _maxPagesPerCycle = maxPagesPerCycle;
    }

    public async Task<PayoReleaseCycleResult> ReleaseDueRoundsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var settledBefore = now.AddSeconds(-_options.ReleaseGraceSeconds);

        var scanned = 0;
        var released = 0;
        var alreadyReleased = 0;
        var refused = 0;
        var backedOff = 0;
        var failures = new List<PayoReleaseFailure>();
        var cursor = _cursors.Get(tenantId);

        for (var page = 0; page < _maxPagesPerCycle; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<RestoReleaseQueries.PendingRow> due;
            {
                await using var queryDb = await _factory.CreateAsync(tenantId, cancellationToken);
                due = await RestoReleaseQueries.DueForReleaseAsync(queryDb, tenantId, settledBefore, cursor, _options.BatchSize, cancellationToken);
            }

            if (due.Count == 0)
            {
                // The queue is exhausted: the next pass starts from the head again.
                _cursors.Set(tenantId, null);
                break;
            }

            foreach (var candidate in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                if (_orderBackoff.IsBackedOff((tenantId, candidate.OrderId), now))
                {
                    backedOff++;
                    cursor = (candidate.SettledAtUtc, candidate.OrderId);
                    _cursors.Set(tenantId, cursor);
                    continue;
                }

                try
                {
                    // A fresh context per order: a failed save must not leave tracked state behind for the next order.
                    await using var db = await _factory.CreateAsync(tenantId, cancellationToken);
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                    var outcome = await PayoRoundReleaseCore.ReleaseAsync(
                        db,
                        new PayoReleaseCommand(tenantId, candidate.OrderId, candidate.RoundId, ActorUserId: null, ExpectedRowVersion: null, BranchScope: null),
                        _timeProvider,
                        cancellationToken);

                    switch (outcome.Outcome)
                    {
                        case PayoReleaseOutcome.Released:
                            await transaction.CommitAsync(cancellationToken);
                            released++;
                            break;
                        case PayoReleaseOutcome.AlreadyReleased:
                            alreadyReleased++;
                            break;
                        default:
                            // Refused or gone: nothing changed, the transaction rolls back on dispose.
                            refused++;
                            break;
                    }

                    _orderBackoff.Clear((tenantId, candidate.OrderId));
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation can surface as a provider exception rather than OperationCanceledException. Either way the
                    // row is not a failure: the cursor stays on the previous row, so this order is examined again on the next pass.
                    throw;
                }
                catch (Exception ex)
                {
                    _orderBackoff.RecordFailure((tenantId, candidate.OrderId), now, _options.FailureBackoffMaxMinutes);
                    failures.Add(new PayoReleaseFailure(candidate.OrderId, candidate.RoundId, Describe(ex)));
                }

                // Progress is saved only after a row is fully handled. A cancelled row is therefore retried, never skipped.
                cursor = (candidate.SettledAtUtc, candidate.OrderId);
                _cursors.Set(tenantId, cursor);
            }

            if (due.Count < _options.BatchSize)
            {
                // A short page means the queue is exhausted for this pass.
                _cursors.Set(tenantId, null);
                break;
            }
        }

        return new PayoReleaseCycleResult(scanned, released, alreadyReleased, refused, failures.Count, failures, backedOff);
    }

    public async Task<int> CountUnacknowledgedAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-_options.UnacknowledgedAlertMinutes);
        await using var db = await _factory.CreateAsync(tenantId, cancellationToken);
        return await RestoReleaseQueries.CountUnacknowledgedAsync(db, tenantId, cutoff, cancellationToken);
    }

    private static string Describe(Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        return message.Length <= 300 ? message : message[..300];
    }
}
