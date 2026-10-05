using Familiar.Agent.Llm;
using Familiar.Domain.Memories;
using Familiar.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Memories;

/// <summary>What the familiar remembers about each user (PLAN.md §6.6).</summary>
/// <remarks>
/// Thresholds measured with nomic-embed-text (2026-10-02): rewordings of one fact score 0.91-0.93
/// against each other, different facts about the same thing ("daughter is Maya" / "son is Leo")
/// 0.78; a short "forget my job" finds its fact at 0.58-0.79 with the next best at least 0.09 behind.
/// </remarks>
public sealed class MemoryStore(IDbContextFactory<FamiliarDbContext> db, IEmbedder embedder, TimeProvider clock, ILogger<MemoryStore> logger)
{
    /// <summary>At or above this, a new fact is the same as one already remembered.</summary>
    internal const double DuplicateSimilarity = 0.88;

    /// <summary>A "forget …" must match at least this well…</summary>
    internal const double ForgetSimilarity = 0.55;

    /// <summary>…and beat the runner-up by this much, or the familiar asks which.</summary>
    internal const double ForgetMargin = 0.06;

    /// <summary>
    /// Slower than this, an embedding is logged: a memory saved in Settings waits for it, and on a
    /// GPU it takes well under a second unless the model has to be loaded.
    /// </summary>
    internal static readonly TimeSpan SlowEmbedding = TimeSpan.FromSeconds(1);

    /// <summary>A sanity limit per account; the oldest conversation-picked memories go first.</summary>
    public const int MaxPerAccount = 500;

    public async Task<IReadOnlyList<MemoryFact>> ListAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Memories.AsNoTracking()
            .Where(m => m.AccountId == accountId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    private readonly SemaphoreSlim _backfilling = new(1, 1);

    /// <summary>
    /// Remembers something, unless it's already remembered (then null). Embedding failures don't lose
    /// the memory; it's stored without one and simply can't be matched by meaning.
    /// </summary>
    /// <remarks>
    /// With <c>embedLater</c> it's saved now and its embedding worked out just after: for a memory
    /// typed in Settings, where the user is waiting on the Save button and has said exactly what they
    /// mean - so only a word-for-word duplicate is turned away, not one that only means the same.
    /// </remarks>
    public async Task<MemoryFact?> AddAsync(
        Guid accountId, string text, MemorySource source, Guid? sourceMessageId, CancellationToken ct, bool embedLater = false)
    {
        text = Normalise(text);
        if (text.Length == 0)
        {
            return null;
        }

        var embedding = embedLater ? null : await TryEmbedAsync(text, EmbeddingPurpose.Document, ct);

        await using var context = await db.CreateDbContextAsync(ct);
        var existing = await context.Memories.AsNoTracking().Where(m => m.AccountId == accountId).ToListAsync(ct);

        if (existing.Any(m => m.Text.Equals(text, StringComparison.OrdinalIgnoreCase)
            || (embedding is not null && m.Embedding is not null && Similarity.Cosine(embedding, m.Embedding) >= DuplicateSimilarity)))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var memory = new MemoryFact
        {
            AccountId = accountId,
            Text = text,
            Embedding = embedding,
            Source = source,
            SourceMessageId = sourceMessageId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Memories.Add(memory);

        if (existing.Count >= MaxPerAccount)
        {
            var oldest = existing.Where(m => m.Source == MemorySource.Conversation).MinBy(m => m.CreatedAt);
            if (oldest is not null)
            {
                context.Memories.Remove(oldest);
            }
        }

        await context.SaveChangesAsync(ct);
        if (embedLater)
        {
            EmbedSoon();
        }

        return memory;
    }

    /// <summary>Changed in Settings: saved now, its embedding worked out just after.</summary>
    public async Task<MemoryFact?> UpdateAsync(Guid accountId, Guid id, string text, CancellationToken ct)
    {
        text = Normalise(text);
        await using var context = await db.CreateDbContextAsync(ct);
        var memory = await context.Memories.FirstOrDefaultAsync(m => m.Id == id && m.AccountId == accountId, ct);
        if (memory is null || text.Length == 0)
        {
            return null;
        }

        memory.Text = text;
        memory.Embedding = null;
        memory.Source = MemorySource.Settings;
        memory.UpdatedAt = clock.GetUtcNow();
        await context.SaveChangesAsync(ct);
        EmbedSoon();
        return memory;
    }

    /// <summary>
    /// Works out the embeddings of memories saved without one: just after a save in Settings, and on
    /// every memory pass, which catches any the model was too busy or away for. One run at a time.
    /// Returns how many it embedded.
    /// </summary>
    public async Task<int> EmbedMissingAsync(CancellationToken ct)
    {
        if (!await _backfilling.WaitAsync(0, ct))
        {
            return 0;
        }

        try
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var missing = await context.Memories.AsNoTracking()
                .Where(m => m.Embedding == null)
                .OrderBy(m => m.UpdatedAt)
                .Take(50)
                .Select(m => new { m.Id, m.Text })
                .ToListAsync(ct);

            var done = 0;
            foreach (var memory in missing)
            {
                if (await TryEmbedAsync(memory.Text, EmbeddingPurpose.Document, ct) is not { } embedding)
                {
                    break; // The model is away; the next pass tries again.
                }

                // Only if it still says what was embedded: an edit in the meantime gets its own.
                done += await context.Memories
                    .Where(m => m.Id == memory.Id && m.Text == memory.Text && m.Embedding == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Embedding, embedding), ct);
            }

            return done;
        }
        finally
        {
            _backfilling.Release();
        }
    }

    private void EmbedSoon() => _ = Task.Run(async () =>
    {
        try
        {
            await EmbedMissingAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.EmbeddingFailed(exception.Message);
        }
    });

    public async Task<bool> DeleteAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Memories.Where(m => m.Id == id && m.AccountId == accountId).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<int> DeleteAllAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Memories.Where(m => m.AccountId == accountId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// What "forget …" refers to: words that appear in exactly one memory, or else the one memory
    /// that's clearly closest in meaning. Several candidates means the familiar should ask.
    /// </summary>
    public async Task<IReadOnlyList<MemoryFact>> FindAsync(Guid accountId, string about, CancellationToken ct)
    {
        var all = await ListAsync(accountId, ct);
        about = about.Trim();

        var byWords = all.Where(m => m.Text.Contains(about, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byWords.Count > 0)
        {
            return byWords;
        }

        if (await TryEmbedAsync(about, EmbeddingPurpose.Query, ct) is not { } query)
        {
            return [];
        }

        var ranked = all
            .Where(m => m.Embedding is not null)
            .Select(m => (Memory: m, Score: Similarity.Cosine(query, m.Embedding!)))
            .Where(r => r.Score >= ForgetSimilarity)
            .OrderByDescending(r => r.Score)
            .ToList();

        if (ranked.Count <= 1 || ranked[0].Score - ranked[1].Score >= ForgetMargin)
        {
            return [.. ranked.Take(1).Select(r => r.Memory)];
        }

        return [.. ranked.TakeWhile(r => ranked[0].Score - r.Score < ForgetMargin).Select(r => r.Memory)];
    }

    /// <summary>One line, trimmed, capped, ending in a full stop.</summary>
    internal static string Normalise(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MemoryFact.MaxTextLength)
        {
            text = text[..MemoryFact.MaxTextLength].TrimEnd();
        }

        return text.Length == 0 || ".!?".Contains(text[^1], StringComparison.Ordinal) ? text : text + ".";
    }

    private async Task<float[]?> TryEmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        try
        {
            return await embedder.EmbedAsync(text, purpose, ct);
        }
        catch (LlmUnavailableException exception)
        {
            logger.EmbeddingFailed(exception.Message);
            return null;
        }
        finally
        {
            if (clock.GetElapsedTime(started) is var elapsed && elapsed >= SlowEmbedding)
            {
                logger.EmbeddingSlow((long)elapsed.TotalMilliseconds);
            }
        }
    }
}
