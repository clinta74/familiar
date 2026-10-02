using System.Runtime.CompilerServices;
using Familiar.Agent.Llm;

namespace Familiar.Server.Tests.Infrastructure;

/// <summary>A scripted model: streams the given fragments, optionally slowly, and records what it was asked.</summary>
public sealed class FakeLlm : ILlmClient
{
    public List<string> Fragments { get; set; } = ["[joy:0.8]", " Hello", " there!"];

    /// <summary>Delay before each fragment, so a test can interrupt mid-reply.</summary>
    public TimeSpan FragmentDelay { get; set; } = TimeSpan.Zero;

    public bool Down { get; set; }

    public IReadOnlyList<ChatMessage>? LastPrompt { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        LastPrompt = messages;

        if (Down)
        {
            throw new LlmUnavailableException("fake model is down");
        }

        foreach (var fragment in Fragments)
        {
            if (FragmentDelay > TimeSpan.Zero)
            {
                await Task.Delay(FragmentDelay, ct);
            }

            ct.ThrowIfCancellationRequested();
            yield return fragment;
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(!Down);
}
