namespace Familiar.Domain.Hooks;

/// <summary>
/// A token another system (Home Assistant, a script) uses to make the familiar say something
/// (PLAN.md §6.5). It can announce and nothing else - not read the conversation, not sign in.
/// </summary>
public sealed class HookToken
{
    public const int MaxPerAccount = 10;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>Whose it is, e.g. "Home Assistant": shown as where an announcement came from.</summary>
    public required string Name { get; set; }

    public required string SecretHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// An outbound webhook: when one of its events happens, Familiar POSTs it there, signed
/// (PLAN.md §6.5). The bridge to Home Assistant automations, n8n and the like.
/// </summary>
public sealed class Webhook
{
    public const int MaxPerAccount = 10;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    public required string Name { get; set; }

    /// <summary>Encrypted: webhook URLs often carry their own secret (Home Assistant's do).</summary>
    public required string ProtectedUrl { get; set; }

    /// <summary>Encrypted HMAC key; the receiver checks <c>X-Familiar-Signature</c> with it.</summary>
    public required string ProtectedSecret { get; init; }

    /// <summary>Which events it gets, e.g. ["timer.done", "reminder.due"].</summary>
    public List<string> Events { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastDeliveredAt { get; set; }

    /// <summary>The last delivery's outcome: "200", or why it failed.</summary>
    public string? LastResult { get; set; }
}

/// <summary>
/// An MCP server whose tools the familiar may use (PLAN.md §6.5): new abilities without a server
/// build. Its tools are fetched, then each is switched on or not by the user.
/// </summary>
public sealed class McpServer
{
    public const int MaxPerAccount = 5;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>Short, lower-case: also the prefix of its tools' names as the model sees them.</summary>
    public required string Name { get; set; }

    /// <summary>Encrypted streamable-HTTP endpoint.</summary>
    public required string ProtectedUrl { get; set; }

    /// <summary>Encrypted bearer token, if the server wants one.</summary>
    public string? ProtectedToken { get; set; }

    /// <summary>The tools it offered when last asked, as JSON (name, description, schema, hints).</summary>
    public string ToolsJson { get; set; } = "[]";

    /// <summary>The tools the user has switched on.</summary>
    public List<string> EnabledTools { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RefreshedAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// The account's Home Assistant (BACKLOG item 29): the familiar works the devices exposed to Assist
/// there, by voice, through Assist itself. One per account.
/// </summary>
public sealed class HomeAssistantLink
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>Encrypted base address, e.g. http://homeassistant.local:8123/.</summary>
    public required string ProtectedUrl { get; set; }

    /// <summary>Encrypted long-lived access token.</summary>
    public required string ProtectedToken { get; set; }

    /// <summary>The devices exposed to Assist when last asked, as JSON (id, name, area).</summary>
    public string DevicesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RefreshedAt { get; set; }

    public string? LastError { get; set; }
}
