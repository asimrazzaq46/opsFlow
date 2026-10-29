namespace OpsFlow.Domain.Common;

/// <summary>Marks an entity that belongs to exactly one tenant; it gets an automatic query filter and write protection.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
