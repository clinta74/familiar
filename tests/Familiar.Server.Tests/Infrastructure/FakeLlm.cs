using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Agent.Tools;

namespace Familiar.Server.Tests.Infrastructure;

/// <summary>
/// A scripted model: streams the given fragments, optionally slowly, and records what it was asked.
/// For tool tests, <see cref="Rounds"/> scripts one response per request - a tool call, then the reply
/// that follows its result - before falling back to <see cref="Fragments"/>.
/// </summary>
public sealed class FakeLlm : ILlmClient
{
    public List<string> Fragments { get; set; } = ["[joy:0.8]", " Hello", " there!"];

    /// <summary>Scripted responses, one per request, each a list of chunks.</summary>
    public ConcurrentQueue<IReadOnlyList<LlmChunk>> Rounds { get; } = new();

    /// <summary>Delay before each fragment, so a test can interrupt mid-reply.</summary>
    public TimeSpan FragmentDelay { get; set; } = TimeSpan.Zero;

    public bool Down { get; set; }

    public IReadOnlyList<ChatMessage>? LastPrompt { get; private set; }

    public IReadOnlyList<ToolDefinition>? LastTools { get; private set; }

    /// <summary>Answers to <see cref="CompleteJsonAsync"/>, in order; then <c>{"facts": []}</c>.</summary>
    public ConcurrentQueue<string> JsonAnswers { get; } = new();

    public IReadOnlyList<ChatMessage>? LastJsonPrompt { get; private set; }

    /// <summary>Queues a round in which the model calls a tool and says nothing.</summary>
    public FakeLlm ThenCall(string tool, string argumentsJson)
    {
        Rounds.Enqueue([new ToolCallsChunk([new ToolCall($"call_{Rounds.Count}", tool,
            (JsonObject)JsonNode.Parse(argumentsJson)!)])]);
        return this;
    }

    /// <summary>Queues a round of plain text.</summary>
    public FakeLlm ThenSay(params string[] fragments)
    {
        Rounds.Enqueue([.. fragments.Select(f => (LlmChunk)new TextChunk(f))]);
        return this;
    }

    public async IAsyncEnumerable<LlmChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        LastPrompt = [.. messages];
        LastTools = tools;

        if (Down)
        {
            throw new LlmUnavailableException("fake model is down");
        }

        IReadOnlyList<LlmChunk> chunks = Rounds.TryDequeue(out var round)
            ? round
            : [.. Fragments.Select(f => (LlmChunk)new TextChunk(f))];

        foreach (var chunk in chunks)
        {
            if (FragmentDelay > TimeSpan.Zero)
            {
                await Task.Delay(FragmentDelay, ct);
            }

            ct.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    public Task<JsonObject> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, CancellationToken ct)
    {
        LastJsonPrompt = [.. messages];
        if (Down)
        {
            throw new LlmUnavailableException("fake model is down");
        }

        var answer = JsonAnswers.TryDequeue(out var json) ? json : """{"facts": []}""";
        return Task.FromResult((JsonObject)JsonNode.Parse(answer)!);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(!Down);
}

/// <summary>
/// Embeddings without a model: words hashed into the vector, so texts sharing words are similar and
/// identical texts are identical. Enough to exercise duplicates and "forget …" deterministically.
/// </summary>
public sealed class FakeEmbedder : IEmbedder
{
    public bool Down { get; set; }

    public Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
    {
        if (Down)
        {
            throw new LlmUnavailableException("fake embedder is down");
        }

        var vector = new float[768];
        foreach (var word in text.ToLowerInvariant().Split([' ', '.', ',', '!', '?', '\''],StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 2)
            {
                vector[(int)((uint)StableHash(word) % 768)] += 1;
            }
        }

        return Task.FromResult(vector);
    }

    private static int StableHash(string word)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in word)
            {
                hash = (hash * 31) + c;
            }

            return hash;
        }
    }
}
