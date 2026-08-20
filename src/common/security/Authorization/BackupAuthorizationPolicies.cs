using System.Security.Claims;

namespace FlorisDeV.Security.Authorization;

public static class BackupAuthorizationPolicies
{
    public const string Admin = "BackupAdmin";
    public const string AdminScope = "backup.admin";

    public static bool HasScope(ClaimsPrincipal principal, string requiredScope)
        => principal
            .FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);
}
