namespace Familiar.Server.Auth;

/// <summary>The <c>Auth</c> configuration section. Every key here is bound and logged at startup.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public string CookieName { get; set; } = "familiar.session";

    /// <summary>Sliding: a session used within this many days stays signed in.</summary>
    public int SessionDays { get; set; } = 14;

    /// <summary>
    /// How often a cookie session re-reads its account, so a password change, a disabled account
    /// or a role change reaches sessions that are already open. 0 means every request (tests).
    /// </summary>
    public int RevalidationIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether strangers may register, until an admin chooses on the admin page (it's kept in the
    /// database then). Off, registering needs an invite. The first account can always be created,
    /// because it is how an installation gets its admin.
    /// </summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>
    /// The address people open Familiar at, through any proxy (https://familiar.example.net), for
    /// links the server hands out - invites. Unset, the admin's browser makes the link from the
    /// address it's on.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>Consecutive failures for one username before backoff starts. 0 disables the throttle.</summary>
    public int LoginFailuresBeforeBackoff { get; set; } = 5;

    public int LoginBackoffSeconds { get; set; } = 30;

    public int LoginBackoffMaxSeconds { get; set; } = 900;

    /// <summary>Sliding: a device token used within this many days stays valid.</summary>
    public int DeviceTokenLifetimeDays { get; set; } = 180;

    public int DeviceTokensPerAccount { get; set; } = 10;

    /// <summary>How often a device token's last-used time (and sliding expiry) is written back.</summary>
    public int DeviceTokenTouchMinutes { get; set; } = 60;

    public TimeSpan RevalidationInterval => TimeSpan.FromSeconds(Math.Max(0, RevalidationIntervalSeconds));

    public TimeSpan SessionLifetime => TimeSpan.FromDays(Math.Max(1, SessionDays));

    public TimeSpan DeviceTokenLifetime => TimeSpan.FromDays(Math.Max(1, DeviceTokenLifetimeDays));

    public TimeSpan DeviceTokenTouchInterval => TimeSpan.FromMinutes(Math.Max(0, DeviceTokenTouchMinutes));

    public override string ToString() =>
        $"cookie={CookieName} session={SessionDays}d revalidate={RevalidationIntervalSeconds}s " +
        $"registration={(AllowRegistration ? "open" : "invite-only")} (until an admin chooses) publicUrl={PublicUrl ?? "(from the browser)"} " +
        $"throttle={LoginFailuresBeforeBackoff}x/{LoginBackoffSeconds}s..{LoginBackoffMaxSeconds}s " +
        $"devices={DeviceTokensPerAccount}x{DeviceTokenLifetimeDays}d";
}
