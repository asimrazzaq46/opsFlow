namespace OpsFlow.Domain.Tenants;

public sealed class Tenant
{
    private Tenant()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Slug { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Concurrency token rotated by every operation that can reduce the number of active Owners,
    /// so two concurrent Owner removals cannot both pass the "last Owner" check.
    /// </summary>
    public Guid OwnerChangeStamp { get; private set; }

    public static Tenant Create(Guid id, string name, string slug, DateTime nowUtc) =>
        new()
        {
            Id = id,
            Name = name,
            Slug = slug,
            CreatedAtUtc = nowUtc,
            OwnerChangeStamp = Guid.NewGuid(),
        };

    public void RotateOwnerChangeStamp() => OwnerChangeStamp = Guid.NewGuid();
}
