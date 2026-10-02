namespace Familiar.Server;

/// <summary>
/// Every log line the server writes, as source-generated <see cref="LoggerMessageAttribute"/>
/// methods: no template parsing or boxing per call, and one place to see what is logged.
/// </summary>
internal static partial class ServerLog
{
    [LoggerMessage(1, LogLevel.Information, "Familiar {Version} ({Revision}) starting in {Environment}")]
    public static partial void Starting(this ILogger logger, string version, string revision, string environment);

    [LoggerMessage(2, LogLevel.Information, "Auth: {Options}")]
    public static partial void AuthConfigured(this ILogger logger, string options);

    [LoggerMessage(3, LogLevel.Information, "Forwarded headers: {Description}")]
    public static partial void ProxyConfigured(this ILogger logger, string description);

    [LoggerMessage(10, LogLevel.Information, "Database migrated ({Applied} applied) in {ElapsedMs} ms")]
    public static partial void Migrated(this ILogger logger, int applied, long elapsedMs);

    [LoggerMessage(11, LogLevel.Warning, "Database not reachable yet, retrying in {DelayMs} ms: {Reason}")]
    public static partial void MigrationRetry(this ILogger logger, long delayMs, string reason);

    [LoggerMessage(20, LogLevel.Information, "Registered {Username} as {Role}")]
    public static partial void Registered(this ILogger logger, string username, string role);

    [LoggerMessage(21, LogLevel.Warning, "{Username} became admin as the first account on this installation")]
    public static partial void FirstAccountPromoted(this ILogger logger, string username);

    [LoggerMessage(22, LogLevel.Warning, "Login backoff started for {Username}")]
    public static partial void LoginBackoffStarted(this ILogger logger, string username);

    [LoggerMessage(23, LogLevel.Information, "Password changed for {Username}; other sessions and devices signed out")]
    public static partial void PasswordChanged(this ILogger logger, string username);

    [LoggerMessage(30, LogLevel.Information, "Device {DeviceName} signed in for {Username}")]
    public static partial void DeviceSignedIn(this ILogger logger, string deviceName, string username);

    [LoggerMessage(31, LogLevel.Information, "Device {DeviceId} signed out for {Username}")]
    public static partial void DeviceSignedOut(this ILogger logger, Guid deviceId, string username);

    [LoggerMessage(32, LogLevel.Warning, "Could not record device token use for {DeviceId}")]
    public static partial void DeviceTouchFailed(this ILogger logger, Exception exception, Guid deviceId);
}
