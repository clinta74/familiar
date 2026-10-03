using Familiar.Domain.Conversations;
using Familiar.Domain.Familiars;
using Familiar.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Familiar.Server.Conversation;

/// <summary>
/// Conversations, messages and familiar profiles. A singleton over the context factory, because
/// turns run in the background, outside any request scope.
/// </summary>
public sealed class ConversationStore(IDbContextFactory<FamiliarDbContext> factory, TimeProvider clock)
{
    /// <summary>The account's most recently active conversation, started if there isn't one.</summary>
    public async Task<Domain.Conversations.Conversation> CurrentAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var current = await db.Conversations.AsNoTracking()
            .Where(c => c.AccountId == accountId)
            .OrderByDescending(c => c.LastMessageAt)
            .FirstOrDefaultAsync(ct);

        return current ?? await StartAsync(accountId, ct);
    }

    public async Task<Domain.Conversations.Conversation> StartAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var conversation = new Domain.Conversations.Conversation { AccountId = accountId, StartedAt = now, LastMessageAt = now };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task<Message> AddAsync(Message message, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Messages.Add(message);
        await db.Conversations
            .Where(c => c.Id == message.ConversationId)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.LastMessageAt, message.CreatedAt), ct);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>The <paramref name="limit"/> messages before <paramref name="excluding"/>, oldest first.</summary>
    public async Task<IReadOnlyList<Message>> HistoryAsync(Guid conversationId, Guid excluding, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Id != excluding)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);

        recent.Reverse();
        return recent;
    }

    /// <summary>The current conversation's latest messages, oldest first, for a client opening the app.</summary>
    public async Task<IReadOnlyList<Message>> RecentAsync(Guid accountId, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conversationId = await db.Conversations
            .Where(c => c.AccountId == accountId)
            .OrderByDescending(c => c.LastMessageAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (conversationId is null)
        {
            return [];
        }

        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);

        recent.Reverse();
        return recent;
    }

    /// <summary>
    /// Cuts a reply down to what the user heard before talking over it, and marks it interrupted.
    /// Null if there's no such reply on this account, or nothing to cut.
    /// </summary>
    public async Task<Message?> TruncateReplyAsync(Guid accountId, Guid replyId, string heard, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var reply = await db.Messages
            .Where(m => m.Id == replyId && m.Role == MessageRole.Assistant)
            .Join(db.Conversations.Where(c => c.AccountId == accountId), m => m.ConversationId, c => c.Id, (m, _) => m)
            .SingleOrDefaultAsync(ct);

        if (reply is null || HeardPart(reply.Content, heard) is not { } text)
        {
            return null;
        }

        reply.Content = text;
        reply.Interrupted = true;
        await db.SaveChangesAsync(ct);
        return reply;
    }

    /// <summary>
    /// The start of <paramref name="content"/> that was heard: as many of its words as
    /// <paramref name="heard"/> has. Counted rather than copied, because the player rebuilds what
    /// was heard from the speech's word timings, which drop punctuation and symbols ("hf_tester" is
    /// said as "hftester"); the stored reply keeps its own spelling. Null when it was all heard.
    /// </summary>
    internal static string? HeardPart(string content, string heard)
    {
        var count = heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var end = 0;
        for (var word = 0; word < count; word++)
        {
            while (end < content.Length && char.IsWhiteSpace(content[end]))
            {
                end++;
            }

            if (end == content.Length)
            {
                return null;
            }

            while (end < content.Length && !char.IsWhiteSpace(content[end]))
            {
                end++;
            }
        }

        return content[end..].Trim().Length == 0 ? null : content[..end].Trim();
    }

    /// <summary>What the familiar said last in the current conversation, for the echo guard.</summary>
    public async Task<string?> LastReplyAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .Join(db.Conversations.Where(c => c.AccountId == accountId), m => m.ConversationId, c => c.Id, (m, _) => m)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>When the user and the familiar last exchanged anything before a moment, across conversations.</summary>
    public async Task<DateTimeOffset?> LastMessageAtAsync(Guid accountId, DateTimeOffset before, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Messages
            .Where(m => m.CreatedAt < before)
            .Join(db.Conversations.Where(c => c.AccountId == accountId), m => m.ConversationId, c => c.Id, (m, _) => m)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Claims the right to greet: true for exactly one caller per <paramref name="gap"/>, however many
    /// tabs connect at once - it's a conditional update, so the database settles the race.
    /// </summary>
    public async Task<bool> TryClaimGreetingAsync(Guid accountId, DateTimeOffset now, TimeSpan gap, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = now - gap;
        return await db.FamiliarProfiles
            .Where(p => p.AccountId == accountId && p.Proactive && (p.LastGreetedAt == null || p.LastGreetedAt < since))
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.LastGreetedAt, now), ct) > 0;
    }

    /// <summary>The account's familiar, created with defaults the first time.</summary>
    public async Task<FamiliarProfile> ProfileAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.FamiliarProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.AccountId == accountId, ct) is { } existing)
        {
            return existing;
        }

        var now = clock.GetUtcNow();
        var profile = new FamiliarProfile { AccountId = accountId, CreatedAt = now, UpdatedAt = now };
        db.FamiliarProfiles.Add(profile);

        try
        {
            await db.SaveChangesAsync(ct);
            return profile;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request created it first; theirs is the one.
            await using var retry = await factory.CreateDbContextAsync(ct);
            return await retry.FamiliarProfiles.AsNoTracking().SingleAsync(p => p.AccountId == accountId, ct);
        }
    }

    public async Task<FamiliarProfile> SaveProfileAsync(FamiliarProfile profile, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        profile.UpdatedAt = clock.GetUtcNow();
        db.FamiliarProfiles.Update(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<string> UsernameAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Username).SingleAsync(ct);
    }
}
