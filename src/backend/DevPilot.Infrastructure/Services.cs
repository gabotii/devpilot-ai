using DevPilot.Application.Common;
using DevPilot.Domain.Identity;
using DevPilot.Domain.Organizations;
using DevPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DevPilot.Infrastructure;

/// <summary>Per-request (scoped) tenant state. Set once by the organization authorization filter.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? OrganizationId { get; private set; }
    public OrgRole? Role { get; private set; }

    public void SetOrganization(Guid organizationId, OrgRole role)
    {
        if (OrganizationId is { } existing && existing != organizationId)
        {
            throw new InvalidOperationException("The organization context cannot be changed within a scope.");
        }

        OrganizationId = organizationId;
        Role = role;
    }

    public void EnterSystemScope(Guid organizationId)
    {
        if (OrganizationId is { } existing && existing != organizationId)
        {
            throw new InvalidOperationException("The organization context cannot be changed within a scope.");
        }

        OrganizationId = organizationId;
        Role = null;
    }
}

/// <summary>PBKDF2 (ASP.NET Core Identity v3 format: HMAC-SHA512, 100k iterations, per-hash salt).</summary>
public sealed class IdentityPasswordHasher : Application.Common.IPasswordHasher
{
    private static readonly User Subject = new("hash@local", "hash", DateTimeOffset.UnixEpoch);
    private readonly PasswordHasher<User> _inner = new();

    public string Hash(string password) => _inner.HashPassword(Subject, password);

    public (bool Valid, bool NeedsRehash) Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return (false, false);
        }

        try
        {
            return _inner.VerifyHashedPassword(Subject, hash, password) switch
            {
                PasswordVerificationResult.Success => (true, false),
                PasswordVerificationResult.SuccessRehashNeeded => (true, true),
                _ => (false, false),
            };
        }
        catch (FormatException)
        {
            return (false, false);
        }
    }
}

/// <summary>
/// Atomic "UPDATE … RETURNING" so concurrent creates never get the same number. Numbers allocated by a transaction that later
/// fails are skipped (gaps are acceptable, duplicates are not).
/// </summary>
public sealed class WorkItemNumberAllocator(AppDbContext db) : IWorkItemNumberAllocator
{
    public async Task<int> NextAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await db.Database
            .SqlQuery<int>($"UPDATE projects SET next_item_number = next_item_number + 1 WHERE id = {projectId} RETURNING next_item_number - 1 AS \"Value\"")
            .ToListAsync(cancellationToken);
        return result.Count == 1 ? result[0] : throw new NotFoundException("Project");
    }

    public async Task<int> NextIncidentNumberAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var result = await db.Database
            .SqlQuery<int>($"UPDATE organizations SET next_incident_number = next_incident_number + 1 WHERE id = {organizationId} RETURNING next_incident_number - 1 AS \"Value\"")
            .ToListAsync(cancellationToken);
        return result.Count == 1 ? result[0] : throw new NotFoundException("Organization");
    }
}
