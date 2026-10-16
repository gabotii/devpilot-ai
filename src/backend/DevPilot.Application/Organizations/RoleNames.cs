namespace DevPilot.Application.Organizations;

/// <summary>User-facing (Spanish) role names for activity text. The API itself always exposes the enum names.</summary>
public static class RoleNames
{
    public static string Spanish(Domain.Organizations.OrgRole role) => role switch
    {
        Domain.Organizations.OrgRole.Owner => "Propietario",
        Domain.Organizations.OrgRole.Admin => "Administrador",
        Domain.Organizations.OrgRole.Developer => "Desarrollador",
        _ => "Lector",
    };
}
