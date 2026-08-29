using System.Text.Json;
using System.Text.Json.Serialization;

namespace Negosio.IntegrationTests.Infrastructure;

/// <summary>Matches the API's JSON settings (web defaults + string enums).</summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record ApiErrorBody(
    string Code,
    string Message,
    string TraceId,
    Dictionary<string, string[]>? Errors);

public sealed record RegisterResponseBody(Guid TenantId, Guid BranchId, Guid OwnerUserId);
