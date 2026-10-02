using System.Collections.Concurrent;
using Familiar.Agent.Speech;

namespace Familiar.Server.Conversation;

/// <summary>
/// Synthesised sentences, held just long enough for every device to fetch them. The event stream
/// announces each clip by URL (SSE carries text, not audio, PLAN.md §3.1); the client fetches it.
/// </summary>
/// <remarks>
/// In memory and short-lived on purpose: speech is regenerated rather than stored, and nothing about
/// a conversation's audio should outlive the conversation (PLAN.md §9).
/// </remarks>
public sealed class SpeechClipStore(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, Entry> _clips = new();

    public Guid Add(Guid accountId, SpeechClip clip)
    {
        Prune();
        var id = Guid.CreateVersion7();
        _clips[id] = new Entry(accountId, clip, clock.GetUtcNow());
        return id;
    }

    /// <summary>The clip, if it exists and belongs to the account. Someone else's is "not found".</summary>
    public SpeechClip? Get(Guid accountId, Guid clipId) =>
        _clips.TryGetValue(clipId, out var entry) && entry.AccountId == accountId ? entry.Clip : null;

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Lifetime;
        foreach (var (id, entry) in _clips)
        {
            if (entry.Created < cutoff)
            {
                _clips.TryRemove(id, out _);
            }
        }
    }

    private sealed record Entry(Guid AccountId, SpeechClip Clip, DateTimeOffset Created);
}
