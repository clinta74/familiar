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

    [LoggerMessage(55, LogLevel.Information, "Ignored a transcript that was the familiar's own voice: {Transcript}")]
    public static partial void EchoIgnored(this ILogger logger, string transcript);

    [LoggerMessage(54, LogLevel.Information, "Downloaded speech-to-text model {Model}")]
    public static partial void SttModelDownloaded(this ILogger logger, string model);

    [LoggerMessage(52, LogLevel.Debug, "Synthesised {Chars} chars in {ElapsedMs} ms")]
    public static partial void SentenceSynthesized(this ILogger logger, int chars, long elapsedMs);

    // The numbers phase 2's "done when" is measured by (PLAN.md §11): from the user finishing their
    // message to the first words and the first audio. -1 means not applicable (typed, or not spoken).
    [LoggerMessage(53, LogLevel.Information, "Turn: transcribe {TranscribeMs} ms, first text {FirstTextMs} ms, first audio {FirstAudioMs} ms, total {TotalMs} ms")]
    public static partial void TurnTiming(this ILogger logger, long transcribeMs, long firstTextMs, long firstAudioMs, long totalMs);

    [LoggerMessage(60, LogLevel.Information, "Tools: {Tools}")]
    public static partial void ToolsConfigured(this ILogger logger, string tools);

    [LoggerMessage(61, LogLevel.Information, "Tool {Tool}: {Outcome} in {ElapsedMs} ms")]
    public static partial void ToolRan(this ILogger logger, string tool, Familiar.Domain.Tools.ToolOutcome outcome, long elapsedMs);

    [LoggerMessage(62, LogLevel.Warning, "The model asked for a tool that doesn't exist: {Tool}")]
    public static partial void UnknownTool(this ILogger logger, string tool);

    [LoggerMessage(63, LogLevel.Error, "Tool {Tool} failed")]
    public static partial void ToolFailed(this ILogger logger, Exception exception, string tool);

    [LoggerMessage(70, LogLevel.Information, "{Kind} {JobId} fired ({LateSeconds} s after it was due)")]
    public static partial void JobFired(this ILogger logger, Familiar.Domain.Scheduling.JobKind kind, Guid jobId, long lateSeconds);

    [LoggerMessage(71, LogLevel.Error, "Scheduler pass failed; retrying shortly")]
    public static partial void SchedulerFailed(this ILogger logger, Exception exception);

    [LoggerMessage(72, LogLevel.Error, "Couldn't announce job {JobId}")]
    public static partial void AnnounceFailed(this ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(80, LogLevel.Information, "Push subscription at {Host} is gone; removed it")]
    public static partial void PushSubscriptionGone(this ILogger logger, string host);

    [LoggerMessage(81, LogLevel.Warning, "Push service {Host} rejected a push: {Status} {Detail}")]
    public static partial void PushRejected(this ILogger logger, string host, int status, string detail);

    [LoggerMessage(82, LogLevel.Warning, "Couldn't reach push service {Host}: {Reason}")]
    public static partial void PushFailed(this ILogger logger, string host, string reason);

    [LoggerMessage(90, LogLevel.Warning, "Couldn't embed text for memory: {Reason}")]
    public static partial void EmbeddingFailed(this ILogger logger, string reason);

    [LoggerMessage(91, LogLevel.Error, "Memory pass failed; retrying shortly")]
    public static partial void MemoryPassFailed(this ILogger logger, Exception exception);

    [LoggerMessage(92, LogLevel.Information, "Memory pass for account {AccountId}: read {Messages} messages, stored {Stored} new memories")]
    public static partial void MemoriesExtracted(this ILogger logger, Guid accountId, int messages, int stored);

    [LoggerMessage(93, LogLevel.Error, "Couldn't greet account {AccountId}")]
    public static partial void GreetingFailed(this ILogger logger, Exception exception, Guid accountId);

    [LoggerMessage(100, LogLevel.Information, "Calendar feed {SourceId} synced: {Events} events")]
    public static partial void FeedSynced(this ILogger logger, Guid sourceId, int events);

    [LoggerMessage(101, LogLevel.Warning, "Calendar feed {SourceId} failed: {Error}")]
    public static partial void FeedFailed(this ILogger logger, Guid sourceId, string error);

    [LoggerMessage(102, LogLevel.Error, "Calendar sync pass failed; retrying shortly")]
    public static partial void FeedPassFailed(this ILogger logger, Exception exception);

    [LoggerMessage(110, LogLevel.Error, "Couldn't dispatch webhook event {Event}")]
    public static partial void WebhookFailed(this ILogger logger, Exception exception, string @event);

    [LoggerMessage(111, LogLevel.Warning, "Webhook {WebhookId} didn't take {Event}: {Result}")]
    public static partial void WebhookRejected(this ILogger logger, Guid webhookId, string @event, string result);

    [LoggerMessage(45, LogLevel.Information, "Models loaded and kept warm: {Model}, {EmbeddingModel}")]
    public static partial void ModelsWarmed(this ILogger logger, string model, string embeddingModel);

    [LoggerMessage(46, LogLevel.Warning, "Model {Model} doesn't exist on the model server. Create it once with tools/ollama/create-models.sh (see the NAS compose file)")]
    public static partial void ModelMissing(this ILogger logger, string model);

    [LoggerMessage(24, LogLevel.Warning, "{Admin} created account {Username} as {Role}")]
    public static partial void AdminCreatedUser(this ILogger logger, string admin, string username, string role);

    [LoggerMessage(25, LogLevel.Warning, "{Admin} made {Username} {Role}")]
    public static partial void AdminChangedRole(this ILogger logger, string admin, string username, string role);

    [LoggerMessage(26, LogLevel.Warning, "{Admin} set {Username} disabled={Disabled}")]
    public static partial void AdminSetDisabled(this ILogger logger, string admin, string username, bool disabled);

    [LoggerMessage(27, LogLevel.Warning, "{Admin} reset the password of {Username}; their sessions and devices were signed out")]
    public static partial void AdminResetPassword(this ILogger logger, string admin, string username);

    [LoggerMessage(28, LogLevel.Warning, "{Admin} deleted account {Username} and everything in it")]
    public static partial void AdminDeletedUser(this ILogger logger, string admin, string username);
}
