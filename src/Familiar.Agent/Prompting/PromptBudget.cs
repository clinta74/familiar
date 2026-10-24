using Familiar.Agent.Llm;
using Familiar.Agent.Tools;

namespace Familiar.Agent.Prompting;

/// <summary>
/// Keeps a prompt inside the model's window. Ollama doesn't refuse a prompt that's too long: it
/// fills the window and cuts the reply off (on the NAS, mid tool call). The window holds the tools,
/// the prompt and the reply, so the history gives way, oldest first.
/// </summary>
public static class PromptBudget
{
    /// <summary>
    /// Characters per token, on the low side so the estimate errs long: gemma's are nearer four for
    /// English, but JSON schemas and names run shorter.
    /// </summary>
    public const double CharsPerToken = 3.0;

    /// <summary>What a picture costs, near enough (gemma's encoder gives each a fixed number of tokens).</summary>
    public const int TokensPerImage = 300;

    /// <summary>A rough count of the tokens a prompt and its tools take: chat formatting included, by a margin.</summary>
    public static int Estimate(IReadOnlyList<ChatMessage> prompt, IReadOnlyList<ToolDefinition>? tools)
    {
        var characters = prompt.Sum(m => m.Content.Length + 10)
            + (tools?.Sum(t => t.Name.Length + t.Description.Length + t.Parameters.ToJsonString().Length + 40) ?? 0);
        var images = prompt.Sum(m => m.Images?.Count ?? 0);
        return (int)Math.Ceiling(characters / CharsPerToken) + (images * TokensPerImage);
    }

    /// <summary>
    /// The history to build the prompt from: all of it if it fits <paramref name="budget"/> tokens with
    /// the tools, else without its oldest messages, <paramref name="step"/> at a time (as the history is
    /// already trimmed, so its start, and the model server's cache of it, holds still for a while).
    /// The newest message and the system prompt always stay.
    /// </summary>
    /// <returns>The history kept, the prompt built from it, and how many messages were left out.</returns>
    public static (IReadOnlyList<T> History, IReadOnlyList<ChatMessage> Prompt, int Dropped) Fit<T>(
        IReadOnlyList<T> history, Func<IReadOnlyList<T>, IReadOnlyList<ChatMessage>> build, IReadOnlyList<ToolDefinition>? tools, int budget, int step)
    {
        var kept = history;
        var prompt = build(kept);
        while (kept.Count > 0 && Estimate(prompt, tools) > budget)
        {
            kept = [.. kept.Skip(Math.Min(step, kept.Count))];
            prompt = build(kept);
        }

        return (kept, prompt, history.Count - kept.Count);
    }
}
