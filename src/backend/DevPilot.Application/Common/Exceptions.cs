namespace DevPilot.Application.Common;

/// <summary>Resource does not exist or is not visible to the caller (we never distinguish the two to avoid leaking existence).</summary>
public sealed class NotFoundException(string resource) : Exception($"{resource} was not found.")
{
    public string Resource { get; } = resource;
}

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.") : Exception(message);

public sealed class UnauthorizedException(string message = "Authentication is required.") : Exception(message);

public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A downstream dependency (e.g. the AI service) failed or is unavailable.</summary>
public sealed class DependencyUnavailableException(string dependency, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public string Dependency { get; } = dependency;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
