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

    [LoggerMessage(40, LogLevel.Information, "Model: {Options}")]
    public static partial void LlmConfigured(this ILogger logger, string options);

    [LoggerMessage(41, LogLevel.Warning, "Model unavailable: {Reason}")]
    public static partial void ModelUnavailable(this ILogger logger, string reason);

    [LoggerMessage(42, LogLevel.Error, "Turn failed for account {AccountId}")]
    public static partial void TurnFailed(this ILogger logger, Exception exception, Guid accountId);

    // Debug: the fallback handles it. Frequent ones mean the system prompt or model drifted from
    // the tag grammar, which is worth knowing when changing either (tools/ollama/README.md).
    [LoggerMessage(43, LogLevel.Debug, "Reply for account {AccountId} had no emotion tag; used the fallback")]
    public static partial void ReplyUntagged(this ILogger logger, Guid accountId);

    // Information, not Debug: these are the gestures the model wants and doesn't have. Grep for them
    // after real use to decide what to add next (the way laugh was found).
    [LoggerMessage(44, LogLevel.Information, "Dropped a tag the familiar can't act out: {Tag}")]
    public static partial void TagIgnored(this ILogger logger, string tag);

    [LoggerMessage(50, LogLevel.Information, "Speech: {Options}")]
    public static partial void SpeechConfigured(this ILogger logger, string options);

    [LoggerMessage(51, LogLevel.Warning, "Speech unavailable: {Reason}")]
    public static partial void SpeechUnavailable(this ILogger logger, string reason);

    [LoggerMessage(54, LogLevel.Information, "Downloaded speech-to-text model {Model}")]
    public static partial void SttModelDownloaded(this ILogger logger, string model);

    [LoggerMessage(52, LogLevel.Debug, "Synthesised {Chars} chars in {ElapsedMs} ms")]
    public static partial void SentenceSynthesized(this ILogger logger, int chars, long elapsedMs);

    // The numbers phase 2's "done when" is measured by (PLAN.md §11): from the user finishing their
    // message to the first words and the first audio. -1 means not applicable (typed, or not spoken).
    [LoggerMessage(53, LogLevel.Information, "Turn: transcribe {TranscribeMs} ms, first text {FirstTextMs} ms, first audio {FirstAudioMs} ms, total {TotalMs} ms")]
    public static partial void TurnTiming(this ILogger logger, long transcribeMs, long firstTextMs, long firstAudioMs, long totalMs);
}
