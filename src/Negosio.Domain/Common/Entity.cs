namespace Negosio.Domain.Common;

/// <summary>
/// Base type for persisted aggregate roots. Keeps identity and audit timestamps consistent
/// across the domain without introducing a generic repository or heavier infrastructure.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    public DateTime CreatedAtUtc { get; protected set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; protected set; } = DateTime.UtcNow;

    protected void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
