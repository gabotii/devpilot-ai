namespace DevPilot.Domain.Common;

/// <summary>Base type for persisted entities. Ids are time-ordered UUIDv7 to keep b-tree inserts cheap.</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
}

/// <summary>Marks entities that belong to exactly one organization (tenant). Used for tenant query filters.</summary>
public interface IOrganizationScoped
{
    Guid OrganizationId { get; }
}

/// <summary>A business rule was violated. Mapped to HTTP 409/422 at the API boundary.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
