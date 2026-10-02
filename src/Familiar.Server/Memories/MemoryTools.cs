using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Memories;
using Familiar.Domain.Tools;

namespace Familiar.Server.Memories;

/// <summary>remember: the user asked the familiar to remember something.</summary>
public sealed class RememberTool(MemoryStore memories) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "remember",
        "Remember a fact about the user for the long term, when they ask you to.",
        ToolSchema.Object(["fact"],
            ("fact", ToolSchema.String("One short sentence about the user, e.g. \"Their sister lives in Leeds.\""))),
        ToolRisk.WriteOwn);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (ToolArgs.Label(arguments, "fact", MemoryFact.MaxTextLength) is not { } fact)
        {
            return ToolResult.Failure("Nothing to remember was given. Ask the user what to remember.");
        }

        var memory = await memories.AddAsync(context.AccountId, fact, MemorySource.Told, null, ct);
        return memory is null
            ? ToolResult.Success(new JsonObject { ["ok"] = true, ["note"] = "You already remembered that." }, "Already remembered")
            : ToolResult.Success(new JsonObject { ["ok"] = true, ["remembered"] = memory.Text }, $"Remembered: {memory.Text}");
    }
}

/// <summary>forget: the user asked the familiar to forget something it remembers.</summary>
public sealed class ForgetTool(MemoryStore memories) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "forget",
        "Forget something you remember about the user, when they ask you to.",
        ToolSchema.Object(["about"],
            ("about", ToolSchema.String("What to forget, in a few words, e.g. \"my sister\" or \"the tea\"."))),
        ToolRisk.WriteOwn);

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        if (ToolArgs.Label(arguments, "about") is not { } about)
        {
            return ToolResult.Failure("Ask the user what to forget.");
        }

        var found = await memories.FindAsync(context.AccountId, about, ct);
        switch (found.Count)
        {
            case 0:
                return ToolResult.Failure($"You don't remember anything about \"{about}\".");

            case 1:
                await memories.DeleteAsync(context.AccountId, found[0].Id, ct);
                return ToolResult.Success(new JsonObject { ["ok"] = true, ["forgot"] = found[0].Text }, $"Forgot: {found[0].Text}");

            default:
                var matches = new JsonArray([.. found.Take(5).Select(m => (JsonNode)JsonValue.Create(m.Text)!)]);
                return new ToolResult(false,
                    new JsonObject { ["ok"] = false, ["error"] = "Several memories match. Ask the user which one.", ["matches"] = matches },
                    null);
        }
    }
}
