namespace Negosio.Application.Resto;

/// <summary>Configuration for the PAYO release worker and the kitchen-ticket alert (section <c>Resto:Reconciliation</c>).</summary>
public sealed class RestoReconciliationOptions
{
    /// <summary>Set false to run no background worker (manual release and the list endpoints still work).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between worker cycles. Allowed 10–15.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Seconds after settlement before the worker releases a Pay-as-you-order round. Allowed 0–300.</summary>
    public int ReleaseGraceSeconds { get; set; } = 30;

    /// <summary>Settled orders the worker examines per tenant per page. Allowed 1–500.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Minutes a released ticket may stay unacknowledged before it is reported. Allowed 1–120.</summary>
    public int UnacknowledgedAlertMinutes { get; set; } = 5;

    /// <summary>Cap, in minutes, for the exponential backoff applied to a failing order or tenant. Allowed 1–60.</summary>
    public int FailureBackoffMaxMinutes { get; set; } = 10;

    /// <summary>Returns every violated rule. Empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PollIntervalSeconds is < 10 or > 15) errors.Add("PollIntervalSeconds must be between 10 and 15.");
        if (ReleaseGraceSeconds is < 0 or > 300) errors.Add("ReleaseGraceSeconds must be between 0 and 300.");
        if (BatchSize is < 1 or > 500) errors.Add("BatchSize must be between 1 and 500.");
        if (UnacknowledgedAlertMinutes is < 1 or > 120) errors.Add("UnacknowledgedAlertMinutes must be between 1 and 120.");
        if (FailureBackoffMaxMinutes is < 1 or > 60) errors.Add("FailureBackoffMaxMinutes must be between 1 and 60.");
        return errors;
    }
}
