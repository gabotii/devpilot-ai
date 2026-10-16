using DevPilot.Domain.Common;

namespace DevPilot.Domain.Notifications;

/// <summary>In-app notification for one user. <see cref="DedupeKey"/> is unique per user so retried events never notify twice.</summary>
public sealed class Notification : Entity, IOrganizationScoped
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 1000;

    private Notification() { }

    public Notification(Guid organizationId, Guid userId, string kind, string title, string? body, string? link, string? dedupeKey, DateTimeOffset now)
    {
        OrganizationId = organizationId;
        UserId = userId;
        Kind = kind;
        Title = Truncate(title, MaxTitleLength)!;
        Body = Truncate(body, MaxBodyLength);
        Link = link;
        DedupeKey = dedupeKey;
        CreatedAt = now;
    }

    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string? Body { get; private set; }

    /// <summary>Relative in-app path (never an external URL) so a notification cannot be used for open redirects.</summary>
    public string? Link { get; private set; }

    public string? DedupeKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    private static string? Truncate(string? value, int max) => value is { } v && v.Length > max ? v[..(max - 1)] + "…" : value;
}
