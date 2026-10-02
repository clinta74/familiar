using System.Security.Claims;
using Familiar.Domain.Accounts;

namespace Familiar.Server.Auth;

/// <summary>The one place a principal is built, for cookies, the revalidator and device tokens alike.</summary>
public static class Principals
{
    /// <summary>Present on principals authenticated by a device token; the token's id.</summary>
    public const string DeviceClaim = "familiar:device";

    public static ClaimsPrincipal Build(Account account, string authenticationType, Guid? deviceId = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new(ClaimTypes.Name, account.Username),
            new(ClaimTypes.Role, account.Role.ToString()),
        ];

        if (deviceId is { } id)
        {
            claims.Add(new Claim(DeviceClaim, id.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    public static Guid? AccountId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? DeviceId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(DeviceClaim), out var id) ? id : null;
}
