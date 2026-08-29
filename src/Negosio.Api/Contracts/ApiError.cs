namespace Negosio.Api.Contracts;

/// <summary>Consistent error envelope returned by every failing endpoint.</summary>
public sealed record ApiError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required string TraceId { get; init; }

    /// <summary>Present only for validation failures: field name -> messages.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
