using System.Text.Json.Nodes;
using Familiar.Domain.Tools;

namespace Familiar.Agent.Tools;

/// <summary>What the model is told about a tool: its name, what it does, its JSON-schema parameters.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters, ToolRisk Risk);

/// <summary>The model asking for a tool to be run.</summary>
public sealed record ToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>Who a tool is running for, and when and where they are.</summary>
/// <param name="AccountId">Whose familiar it is.</param>
/// <param name="ReplyId">The reply the tool is part of.</param>
/// <param name="Zone">The user's time zone.</param>
/// <param name="Now">When the turn started.</param>
/// <param name="LookUps">The user lets it look things up outside (the weather, a dictionary).</param>
/// <param name="HomePlace">Where the user lives, as said ("Warren, Michigan, United States"), for when they don't say where.</param>
/// <param name="From">
/// Where the message came from: the phone's device id, "browser", or null (a greeting, a hook) - so
/// "where am I" asks that device.
/// </param>
/// <param name="Home">
/// The place they picked as home in Settings, when they have (BACKLOG item 32): the look-ups use it
/// as it is rather than finding <paramref name="HomePlace"/> by name again, and guessing which Warren.
/// </param>
/// <param name="UserLinks">
/// The web addresses the user wrote in their recent messages: the only pages read_link may read
/// (BACKLOG item 34), so text from elsewhere can't send it to one.
/// </param>
public sealed record ToolContext(
    Guid AccountId, Guid ReplyId, TimeZoneInfo Zone, DateTimeOffset Now, bool LookUps = false, string? HomePlace = null, string? From = null,
    HomeLocation? Home = null, IReadOnlyList<string>? UserLinks = null)
{
    /// <summary>What this reply's tools have done so far.</summary>
    public ToolTurn Turn { get; } = new();
}

/// <summary>Where the user lives, as picked in Settings: a named place and its coordinates.</summary>
/// <param name="Label">"Warren, Michigan, United States".</param>
/// <param name="Name">"Warren".</param>
/// <param name="Region">Its state, province or nation ("Michigan"); empty if it has none.</param>
/// <param name="CountryCode">ISO 3166 alpha-2, "US".</param>
/// <param name="Latitude">Rounded to about a kilometre.</param>
/// <param name="Longitude">Rounded to about a kilometre.</param>
public sealed record HomeLocation(string Label, string Name, string Region, string CountryCode, double Latitude, double Longitude);

/// <summary>A reply's tool calls so far: how many look-ups, which taint what follows.</summary>
public sealed class ToolTurn
{
    public int LookUps { get; set; }

    /// <summary>
    /// Someone else's words are already in the turn - a picture's (BACKLOG item 35) - so, as after a
    /// look-up, anything that changes something asks first.
    /// </summary>
    public bool OutsideText { get; set; }

    /// <summary>The pages its tools found, numbered from 1 in order: what "[link:2]" in the reply means.</summary>
    public List<LinkRef> Links { get; } = [];
}

/// <summary>A web page a tool found - its source, an official site - that the familiar may hand over (BACKLOG item 34).</summary>
/// <param name="Url">Its address: http or https, from the tool, never from the model.</param>
/// <param name="Title">What to call it on the card: "Michigan's 10th congressional district".</param>
public sealed record LinkRef(string Url, string Title);

/// <summary>
/// What a tool did. <see cref="Data"/> goes back to the model, which turns it into speech;
/// <see cref="Summary"/> is the short line the user sees in the transcript ("Timer set: pasta, 10 min").
/// </summary>
public sealed record ToolResult(bool Ok, JsonObject Data, string? Summary)
{
    /// <summary>Pages the familiar may show the user. The model sees them by number and title, never their addresses.</summary>
    public IReadOnlyList<LinkRef> Links { get; init; } = [];

    /// <summary>
    /// The reply ends with these words, said as they are, instead of the model being asked again: for
    /// a question the user has to hear exactly - a text read back before it's sent (BACKLOG item 39).
    /// </summary>
    public string? FinalWords { get; init; }

    public static ToolResult Success(JsonObject data, string summary) => new(true, data, summary);

    /// <summary>The model is told why, so it can say so or ask the user something.</summary>
    public static ToolResult Failure(string error, string? summary = null) =>
        new(false, new JsonObject { ["ok"] = false, ["error"] = error }, summary);
}

/// <summary>One thing the familiar can do (PLAN.md §6.1).</summary>
public interface ITool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct);

    /// <summary>What the user is asked to allow, for tools that need confirming ("Add 'dentist' to your calendar").</summary>
    string Describe(JsonObject arguments) => Definition.Name;
}

/// <summary>
/// Runs tool calls for a turn: the server's implementation adds what the agent shouldn't know about -
/// the audit trail, and asking the user before anything that reaches outside Familiar.
/// </summary>
public interface IToolExecutor
{
    /// <summary>The tools this user's familiar has: the built-in ones, and any from their MCP servers.</summary>
    Task<IReadOnlyList<ToolDefinition>> DefinitionsAsync(ToolContext context, CancellationToken ct);

    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct);
}
