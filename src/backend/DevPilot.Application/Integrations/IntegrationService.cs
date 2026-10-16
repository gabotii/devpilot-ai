using System.Security.Cryptography;
using DevPilot.Application.Common;
using DevPilot.Domain.Integrations;
using DevPilot.Domain.Organizations;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevPilot.Application.Integrations;

public sealed class IntegrationOptions
{
    public const string SectionName = "Integrations";

    /// <summary>Public base URL of this deployment, used to build webhook URLs shown to admins (e.g. https://devpilot.example.com).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5173";
}

public sealed record IntegrationDto(Guid Id, IntegrationProvider Provider, string Name, IntegrationStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset? LastEventAt, DateTimeOffset? VerifiedAt, DateTimeOffset? LastErrorAt, string? LastError, long EventCount, string? WebhookUrl,
    int RepositoryCount, int FailedDeliveries24h, string? ExternalAccount);

/// <summary>Returned once at creation/rotation: the secret is never retrievable afterwards.</summary>
public sealed record IntegrationSecretDto(IntegrationDto Integration, string WebhookUrl, string Secret, IReadOnlyList<string> Instructions);

public sealed record CreateIntegrationRequest(string Name);

public sealed record SetIntegrationStatusRequest(IntegrationStatus Status);

public sealed record RepositoryDto(Guid Id, Guid IntegrationId, string FullName, string? DefaultBranch, string? HtmlUrl, RepositoryStatus Status,
    Guid? ProjectId, string? ProjectName, DateTimeOffset FirstSeenAt, DateTimeOffset LastEventAt);

public sealed record ConfigureRepositoryRequest(RepositoryStatus Status, Guid? ProjectId);

public sealed record DeliveryDto(Guid Id, string DeliveryId, string EventType, string? Action, string? RepositoryFullName, DeliveryStatus Status,
    int Attempts, string? Error, DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt, DateTimeOffset? NextAttemptAt, bool PayloadAvailable);

public sealed record IngestKeyDto(Guid Id, string Name, string Prefix, IngestScopes Scopes, Guid? ProjectId, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

public sealed record CreateIngestKeyRequest(string Name, Guid? ProjectId, IngestScopes Scopes);

public sealed record IngestKeyCreatedDto(IngestKeyDto Key, string Secret);

public sealed class CreateIntegrationRequestValidator : AbstractValidator<CreateIntegrationRequest>
{
    public CreateIntegrationRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Integration.MaxNameLength);
}

public sealed class CreateIngestKeyRequestValidator : AbstractValidator<CreateIngestKeyRequest>
{
    public CreateIngestKeyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Scopes).Must(s => s != IngestScopes.None && (s & ~IngestScopes.All) == 0).WithMessage("Select at least one valid scope.");
    }
}

/// <summary>Administration of integrations, authorized repositories, webhook deliveries and ingestion keys (Admin+).</summary>
public sealed class IntegrationService(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    ISecretProtector protector,
    WebhookReceiver receiver,
    AuditLog audit,
    TimeProvider clock,
    IOptions<IntegrationOptions> options,
    IValidator<CreateIntegrationRequest> createValidator,
    IValidator<CreateIngestKeyRequest> keyValidator)
{
    public async Task<IReadOnlyList<IntegrationDto>> ListAsync(CancellationToken ct)
    {
        var integrations = await db.Integrations.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var since = clock.GetUtcNow().AddHours(-24);
        var repoCounts = await db.Repositories.AsNoTracking().GroupBy(r => r.IntegrationId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var failed = await db.WebhookDeliveries.AsNoTracking().Where(d => d.Status == DeliveryStatus.Failed && d.ReceivedAt >= since)
            .GroupBy(d => d.IntegrationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return integrations.Select(i => ToDto(i, repoCounts.GetValueOrDefault(i.Id), failed.GetValueOrDefault(i.Id))).ToList();
    }

    public async Task<IntegrationSecretDto> CreateGitHubWebhookAsync(CreateIntegrationRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        RequireAdmin();
        var orgId = tenant.RequireOrganizationId();
        if (await db.Integrations.CountAsync(ct) >= 20)
        {
            throw new ConflictException("integration.limit", "An organization can have at most 20 integrations.");
        }

        var secret = NewSecret();
        var integration = new Integration(orgId, IntegrationProvider.GitHubWebhook, request.Name, protector.Protect(secret), currentUser.RequireUserId(),
            clock.GetUtcNow());
        db.Integrations.Add(integration);
        audit.Record(orgId, "integration.created", "integration", integration.Id.ToString(), new() { ["provider"] = integration.Provider.ToString() });
        await db.SaveChangesAsync(ct);
        return SecretResult(integration, secret);
    }

    public async Task<IntegrationSecretDto> RotateSecretAsync(Guid integrationId, CancellationToken ct)
    {
        RequireAdmin();
        var integration = await LoadAsync(integrationId, ct);
        if (integration.Provider != IntegrationProvider.GitHubWebhook)
        {
            throw new ConflictException("integration.no_secret", "This integration does not use a webhook secret.");
        }

        var secret = NewSecret();
        integration.RotateSecret(protector.Protect(secret));
        audit.Record(integration.OrganizationId, "integration.secret_rotated", "integration", integration.Id.ToString());
        await db.SaveChangesAsync(ct);
        return SecretResult(integration, secret);
    }

    public async Task<IntegrationDto> SetStatusAsync(Guid integrationId, SetIntegrationStatusRequest request, CancellationToken ct)
    {
        RequireAdmin();
        var integration = await LoadAsync(integrationId, ct);
        integration.SetStatus(request.Status);
        audit.Record(integration.OrganizationId, "integration.status_changed", "integration", integration.Id.ToString(), new() { ["status"] = request.Status.ToString() });
        await db.SaveChangesAsync(ct);
        return ToDto(integration, await db.Repositories.CountAsync(r => r.IntegrationId == integrationId, ct), 0);
    }

    /// <summary>Removes the integration, its repositories and deliveries. Recorded pipelines/deployments are kept (history).</summary>
    public async Task DeleteAsync(Guid integrationId, CancellationToken ct)
    {
        RequireAdmin();
        var integration = await LoadAsync(integrationId, ct);
        db.Integrations.Remove(integration);
        audit.Record(integration.OrganizationId, "integration.deleted", "integration", integration.Id.ToString(), new() { ["name"] = integration.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RepositoryDto>> ListRepositoriesAsync(RepositoryStatus? status, CancellationToken ct)
    {
        var query = db.Repositories.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        var repos = await query.OrderBy(r => r.Status).ThenBy(r => r.FullName).Take(500).ToListAsync(ct);
        var projectIds = repos.Where(r => r.ProjectId.HasValue).Select(r => r.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return repos.Select(r => new RepositoryDto(r.Id, r.IntegrationId, r.FullName, r.DefaultBranch, r.HtmlUrl, r.Status, r.ProjectId,
            r.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, r.FirstSeenAt, r.LastEventAt)).ToList();
    }

    public async Task<RepositoryDto> ConfigureRepositoryAsync(Guid repositoryId, ConfigureRepositoryRequest request, CancellationToken ct)
    {
        RequireAdmin();
        if (!Enum.IsDefined(request.Status))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.Status), "Unknown status.")]);
        }

        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct) ?? throw new NotFoundException("Repository");
        string? projectName = null;
        if (request.ProjectId is { } projectId)
        {
            projectName = await db.Projects.Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync(ct)
                ?? throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.ProjectId), "Project not found.")]);
        }

        repo.Configure(request.Status, request.ProjectId);
        audit.Record(repo.OrganizationId, "repository.configured", "repository", repo.Id.ToString(),
            new() { ["status"] = request.Status.ToString(), ["projectId"] = request.ProjectId?.ToString() });
        await db.SaveChangesAsync(ct);
        return new RepositoryDto(repo.Id, repo.IntegrationId, repo.FullName, repo.DefaultBranch, repo.HtmlUrl, repo.Status, repo.ProjectId,
            repo.Status == RepositoryStatus.Authorized ? projectName : null, repo.FirstSeenAt, repo.LastEventAt);
    }

    public async Task<PagedResult<DeliveryDto>> ListDeliveriesAsync(Guid integrationId, DeliveryStatus? status, PageRequest page, CancellationToken ct)
    {
        await LoadAsync(integrationId, ct);
        var query = db.WebhookDeliveries.AsNoTracking().Where(d => d.IntegrationId == integrationId);
        if (status is { } s)
        {
            query = query.Where(d => d.Status == s);
        }

        return await query.OrderByDescending(d => d.ReceivedAt)
            .Select(d => new DeliveryDto(d.Id, d.DeliveryId, d.EventType, d.Action, d.RepositoryFullName, d.Status, d.Attempts, d.Error, d.ReceivedAt,
                d.ProcessedAt, d.NextAttemptAt, d.Payload != null))
            .ToPagedResultAsync(page, ct);
    }

    public async Task<DeliveryDto> RetryDeliveryAsync(Guid integrationId, Guid deliveryId, CancellationToken ct)
    {
        RequireAdmin();
        var integration = await LoadAsync(integrationId, ct);
        var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId && d.IntegrationId == integrationId, ct)
            ?? throw new NotFoundException("Delivery");
        delivery.RequeueManually(clock.GetUtcNow());
        audit.Record(integration.OrganizationId, "integration.delivery_retried", "webhook_delivery", delivery.Id.ToString());
        await receiver.ReprocessAsync(integration, delivery, ct);
        return new DeliveryDto(delivery.Id, delivery.DeliveryId, delivery.EventType, delivery.Action, delivery.RepositoryFullName, delivery.Status,
            delivery.Attempts, delivery.Error, delivery.ReceivedAt, delivery.ProcessedAt, delivery.NextAttemptAt, delivery.Payload != null);
    }

    public async Task<IReadOnlyList<IngestKeyDto>> ListIngestKeysAsync(CancellationToken ct) =>
        await db.IngestKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt)
            .Select(k => new IngestKeyDto(k.Id, k.Name, k.Prefix, k.Scopes, k.ProjectId, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToListAsync(ct);

    public async Task<IngestKeyCreatedDto> CreateIngestKeyAsync(CreateIngestKeyRequest request, CancellationToken ct)
    {
        await keyValidator.ValidateAndThrowAsync(request, ct);
        RequireAdmin();
        var orgId = tenant.RequireOrganizationId();
        if (request.ProjectId is { } projectId && !await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(request.ProjectId), "Project not found.")]);
        }

        var secret = $"dpk_{SecureTokens.Generate(30)}";
        var key = new IngestKey(orgId, request.ProjectId, request.Name, secret[..12], SecureTokens.Hash(secret), request.Scopes, currentUser.RequireUserId(),
            clock.GetUtcNow());
        db.IngestKeys.Add(key);
        audit.Record(orgId, "ingest_key.created", "ingest_key", key.Id.ToString(), new() { ["name"] = key.Name, ["scopes"] = key.Scopes.ToString() });
        await db.SaveChangesAsync(ct);
        return new IngestKeyCreatedDto(new IngestKeyDto(key.Id, key.Name, key.Prefix, key.Scopes, key.ProjectId, key.CreatedAt, null, null), secret);
    }

    public async Task RevokeIngestKeyAsync(Guid keyId, CancellationToken ct)
    {
        RequireAdmin();
        var key = await db.IngestKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct) ?? throw new NotFoundException("Ingest key");
        key.Revoke(clock.GetUtcNow());
        audit.Record(key.OrganizationId, "ingest_key.revoked", "ingest_key", key.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    private IntegrationSecretDto SecretResult(Integration integration, string secret)
    {
        var url = WebhookUrl(integration)!;
        return new IntegrationSecretDto(ToDto(integration, 0, 0), url, secret,
        [
            "En GitHub abre Settings → Webhooks → Add webhook del repositorio u organización.",
            $"Payload URL: {url}",
            "Content type: application/json. Secret: el valor mostrado (solo se muestra una vez).",
            "Eventos: Pushes, Pull requests, Workflow runs, Deployments y Deployment statuses.",
            "Tras el ping inicial, autoriza los repositorios detectados en DevPilot y asígnalos a un proyecto.",
        ]);
    }

    private IntegrationDto ToDto(Integration i, int repoCount, int failed) => new(i.Id, i.Provider, i.Name, i.Status, i.CreatedAt, i.LastEventAt,
        i.VerifiedAt, i.LastErrorAt, i.LastError, i.EventCount, WebhookUrl(i), repoCount, failed, i.ExternalAccount);

    private string? WebhookUrl(Integration i) =>
        i.Provider == IntegrationProvider.GitHubWebhook ? $"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/webhooks/github/{i.Id}" : null;

    private async Task<Integration> LoadAsync(Guid integrationId, CancellationToken ct) =>
        await db.Integrations.FirstOrDefaultAsync(i => i.Id == integrationId, ct) ?? throw new NotFoundException("Integration");

    private void RequireAdmin()
    {
        if (tenant.RequireRole() < OrgRole.Admin)
        {
            throw new ForbiddenException("Only organization admins can manage integrations.");
        }
    }

    private static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
