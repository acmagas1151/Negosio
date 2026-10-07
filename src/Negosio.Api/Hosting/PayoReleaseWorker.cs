using Microsoft.EntityFrameworkCore;
using Negosio.Application.Resto;
using Negosio.Domain.Enums;
using Negosio.Infrastructure.Persistence;

namespace Negosio.Api.Hosting;

/// <summary>
/// Releases settled Pay-as-you-order rounds to the kitchen on a timer, so a round is released even when the cashier never
/// presses release. Runs inside the API host (spec M3, decision D4). Safe with several instances: every release goes through
/// the same locked, idempotent operation the manual action uses.
/// </summary>
/// <remarks>
/// Each cycle visits every active tenant on its own. A tenant that fails (database unreachable, unexpected error) is logged and
/// placed in exponential backoff; the other tenants still run. Backoff state is in memory, so it resets on restart; the database
/// is the source of truth, so a restarted worker simply re-reads what is due.
/// </remarks>
public sealed class PayoReleaseWorker : BackgroundService
{
    private static readonly TimeSpan TenantPassTimeout = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly RestoReconciliationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PayoReleaseWorker> _logger;
    private readonly FailureBackoff<Guid> _tenantBackoff = new();

    /// <summary>Identifies this process in logs when several instances run at once.</summary>
    public string WorkerId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";

    public PayoReleaseWorker(
        IServiceScopeFactory scopes,
        RestoReconciliationOptions options,
        TimeProvider timeProvider,
        ILogger<PayoReleaseWorker> logger)
    {
        _scopes = scopes;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PAYO release worker disabled by configuration (Resto:Reconciliation:Enabled).");
            return;
        }

        _logger.LogInformation(
            "PAYO release worker {WorkerId} started: poll every {PollSeconds}s, release grace {GraceSeconds}s.",
            WorkerId, _options.PollIntervalSeconds, _options.ReleaseGraceSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failure enumerating tenants (the platform database itself) ends this cycle, not the worker.
                _logger.LogError(ex, "PAYO release cycle failed on worker {WorkerId}; retrying next poll.", WorkerId);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PAYO release worker {WorkerId} stopped.", WorkerId);
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantIds = await platform.TenantDatabases.AsNoTracking()
            .Where(t => t.Status == TenantDatabaseStatus.Active)
            .Select(t => t.TenantId)
            .ToListAsync(stoppingToken);

        var worker = scope.ServiceProvider.GetRequiredService<IPayoReleaseWorkerService>();

        foreach (var tenantId in tenantIds)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (_tenantBackoff.IsBackedOff(tenantId, _timeProvider.GetUtcNow().UtcDateTime))
            {
                continue;
            }

            await RunTenantPassAsync(worker, tenantId, stoppingToken);
        }
    }

    private async Task RunTenantPassAsync(IPayoReleaseWorkerService worker, Guid tenantId, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TenantPassTimeout);

            var result = await worker.ReleaseDueRoundsAsync(tenantId, timeout.Token);
            _tenantBackoff.Clear(tenantId);

            if (result.Scanned > 0)
            {
                _logger.LogInformation(
                    "Tenant {TenantId} PAYO release pass on worker {WorkerId}: scanned {Scanned}, released {Released}, already released {AlreadyReleased}, refused {Refused}, failed {Failed}, backed off {BackedOff}.",
                    tenantId, WorkerId, result.Scanned, result.Released, result.AlreadyReleased, result.Refused, result.Failed, result.BackedOff);
            }

            foreach (var failure in result.Failures)
            {
                _logger.LogWarning(
                    "Tenant {TenantId} order {OrderId} round {RoundId} release failed on worker {WorkerId}: {Error}",
                    tenantId, failure.OrderId, failure.RoundId, WorkerId, failure.Error);
            }

            var unacknowledged = await worker.CountUnacknowledgedAsync(tenantId, timeout.Token);
            if (unacknowledged > 0)
            {
                _logger.LogWarning(
                    "Tenant {TenantId} has {Count} released kitchen ticket(s) unacknowledged for more than {Minutes} min (worker {WorkerId}).",
                    tenantId, unacknowledged, _options.UnacknowledgedAlertMinutes, WorkerId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryAfter = _tenantBackoff.RecordFailure(tenantId, _timeProvider.GetUtcNow().UtcDateTime, _options.FailureBackoffMaxMinutes);
            _logger.LogError(
                ex, "Tenant {TenantId} PAYO release pass failed on worker {WorkerId}; skipping until {RetryAfterUtc:o}.",
                tenantId, WorkerId, retryAfter);
        }
    }
}
