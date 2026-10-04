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
/// <param name="HomePlace">Where the user lives, for the weather when they don't say where.</param>
public sealed record ToolContext(Guid AccountId, Guid ReplyId, TimeZoneInfo Zone, DateTimeOffset Now, bool LookUps = false, string? HomePlace = null)
{
    /// <summary>What this reply's tools have done so far.</summary>
    public ToolTurn Turn { get; } = new();
}

/// <summary>A reply's tool calls so far: how many look-ups, which taint what follows.</summary>
public sealed class ToolTurn
{
    public int LookUps { get; set; }
}

/// <summary>
/// What a tool did. <see cref="Data"/> goes back to the model, which turns it into speech;
/// <see cref="Summary"/> is the short line the user sees in the transcript ("Timer set: pasta, 10 min").
/// </summary>
public sealed record ToolResult(bool Ok, JsonObject Data, string? Summary)
{
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
