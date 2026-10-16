using DevPilot.Domain.Common;

namespace DevPilot.Domain.Integrations;

public enum IntegrationProvider
{
    /// <summary>Repository/organization webhook configured manually in GitHub with a DevPilot-generated secret.</summary>
    GitHubWebhook,

    /// <summary>GitHub App installation (requires the App credentials to be configured on the server).</summary>
    GitHubApp,

    /// <summary>Any CI/CD system pushing events through the authenticated ingestion API.</summary>
    Generic,
}

public enum IntegrationStatus
{
    Active,
    Disabled,
}

public sealed class Integration : Entity, IOrganizationScoped
{
    public const int MaxNameLength = 100;

    private Integration() { }

    public Integration(Guid organizationId, IntegrationProvider provider, string name, string? protectedSecret, Guid createdBy, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        Provider = provider;
        Name = name.Trim();
        ProtectedSecret = protectedSecret;
        Status = IntegrationStatus.Active;
        CreatedByUserId = createdBy;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public IntegrationProvider Provider { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public IntegrationStatus Status { get; private set; }

    /// <summary>Webhook signing secret encrypted at rest (AES-GCM). Never returned by the API after creation.</summary>
    public string? ProtectedSecret { get; private set; }

    public long? ExternalInstallationId { get; private set; }
    public string? ExternalAccount { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public string? LastError { get; private set; }
    public long EventCount { get; private set; }

    public bool IsActive => Status == IntegrationStatus.Active;

    public void RotateSecret(string protectedSecret) => ProtectedSecret = protectedSecret;

    public void SetStatus(IntegrationStatus status) => Status = status;

    public void LinkInstallation(long installationId, string? account)
    {
        ExternalInstallationId = installationId;
        ExternalAccount = account;
    }

    public void RecordEvent(DateTimeOffset now)
    {
        LastEventAt = now;
        EventCount++;
    }

    public void MarkVerified(DateTimeOffset now) => VerifiedAt ??= now;

    public void RecordError(string error, DateTimeOffset now)
    {
        LastError = error.Length > 500 ? error[..500] : error;
        LastErrorAt = now;
    }
}

public enum RepositoryStatus
{
    /// <summary>Seen in an event but not yet approved by an admin: its events are recorded but not processed.</summary>
    Pending,
    Authorized,
    Ignored,
}

public sealed class Repository : Entity, IOrganizationScoped
{
    private Repository() { }

    public Repository(Guid organizationId, Guid integrationId, long externalId, string fullName, string? defaultBranch, string? htmlUrl, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        IntegrationId = integrationId;
        ExternalId = externalId;
        FullName = fullName;
        DefaultBranch = defaultBranch;
        HtmlUrl = SafeUrl(htmlUrl);
        Status = RepositoryStatus.Pending;
        FirstSeenAt = now;
        LastEventAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid IntegrationId { get; private set; }
    public long ExternalId { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string? DefaultBranch { get; private set; }
    public string? HtmlUrl { get; private set; }
    public RepositoryStatus Status { get; private set; }
    public Guid? ProjectId { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastEventAt { get; private set; }

    public void Touch(string fullName, string? defaultBranch, DateTimeOffset now)
    {
        FullName = fullName;
        DefaultBranch = defaultBranch ?? DefaultBranch;
        LastEventAt = now;
    }

    public void Configure(RepositoryStatus status, Guid? projectId)
    {
        Status = status;
        ProjectId = status == RepositoryStatus.Authorized ? projectId : null;
    }

    /// <summary>Only https links to github.com are stored, so payload content can never inject javascript: or other schemes.</summary>
    public static string? SafeUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
}

public enum DeliveryStatus
{
    Received,
    Processed,
    Ignored,
    Failed,
}

/// <summary>Inbox record of an inbound webhook. The (integration, delivery id) pair is unique, which makes receipt idempotent.</summary>
public sealed class WebhookDelivery : Entity, IOrganizationScoped
{
    public const int MaxAttempts = 5;

    private WebhookDelivery() { }

    public WebhookDelivery(Guid organizationId, Guid integrationId, string deliveryId, string eventType, string? action, string payload,
        string payloadSha256, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        IntegrationId = integrationId;
        DeliveryId = deliveryId;
        EventType = eventType;
        Action = action;
        Payload = payload;
        PayloadSha256 = payloadSha256;
        Status = DeliveryStatus.Received;
        ReceivedAt = now;
        NextAttemptAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid IntegrationId { get; private set; }
    public string DeliveryId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string? Action { get; private set; }
    public string? RepositoryFullName { get; private set; }

    /// <summary>Raw payload kept for reprocessing; cleared by the retention job.</summary>
    public string? Payload { get; private set; }

    public string PayloadSha256 { get; private set; } = string.Empty;
    public DeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public void SetRepository(string? fullName) => RepositoryFullName = fullName;

    public void MarkProcessed(DateTimeOffset now)
    {
        Attempts++;
        Status = DeliveryStatus.Processed;
        ProcessedAt = now;
        Error = null;
        NextAttemptAt = null;
    }

    public void MarkIgnored(string reason, DateTimeOffset now)
    {
        Attempts++;
        Status = DeliveryStatus.Ignored;
        ProcessedAt = now;
        Error = reason;
        NextAttemptAt = null;
    }

    /// <summary>Exponential backoff (30s, 2m, 8m, 32m); gives up after <see cref="MaxAttempts"/>.</summary>
    public void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        Status = DeliveryStatus.Failed;
        Error = error.Length > 1000 ? error[..1000] : error;
        NextAttemptAt = Attempts >= MaxAttempts ? null : now.AddSeconds(30 * Math.Pow(4, Attempts - 1));
    }

    public void RequeueManually(DateTimeOffset now)
    {
        if (Payload is null)
        {
            throw new DomainException("delivery.payload_purged", "The payload of this delivery was removed by the retention policy.");
        }

        Status = DeliveryStatus.Received;
        NextAttemptAt = now;
        Attempts = 0;
    }

    public void PurgePayload() => Payload = null;
}

[Flags]
public enum IngestScopes
{
    None = 0,
    Logs = 1,
    Deployments = 2,
    Pipelines = 4,
    All = Logs | Deployments | Pipelines,
}

/// <summary>API key for the ingestion API. Only the SHA-256 hash and a display prefix are stored.</summary>
public sealed class IngestKey : Entity, IOrganizationScoped
{
    private IngestKey() { }

    public IngestKey(Guid organizationId, Guid? projectId, string name, string prefix, string keyHash, IngestScopes scopes, Guid createdBy, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
        Name = name.Trim();
        Prefix = prefix;
        KeyHash = keyHash;
        Scopes = scopes;
        CreatedByUserId = createdBy;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Prefix { get; private set; } = string.Empty;
    public string KeyHash { get; private set; } = string.Empty;
    public IngestScopes Scopes { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void Touch(DateTimeOffset now)
    {
        if (LastUsedAt is null || now - LastUsedAt > TimeSpan.FromMinutes(1))
        {
            LastUsedAt = now;
        }
    }
}
