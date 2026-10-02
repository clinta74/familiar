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

        var text = heard.Trim();
        if (reply is null || text.Length >= reply.Content.Length)
        {
            return null;
        }

        reply.Content = text;
        reply.Interrupted = true;
        await db.SaveChangesAsync(ct);
        return reply;
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
