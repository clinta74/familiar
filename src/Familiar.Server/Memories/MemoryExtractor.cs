using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Domain.Conversations;
using Familiar.Domain.Memories;
using Familiar.Persistence;
using Microsoft.EntityFrameworkCore;
using ConversationRecord = Familiar.Domain.Conversations.Conversation;

namespace Familiar.Server.Memories;

public sealed class MemoryOptions
{
    public const string Section = "Memory";

    /// <summary>Picking out memories can be switched off entirely; told memories still work.</summary>
    public bool ExtractFromConversations { get; init; } = true;

    /// <summary>
    /// A conversation is read for memories once it's been quiet this long - so it doesn't compete
    /// with the user for the GPU, and sees whole exchanges rather than half of one.
    /// </summary>
    public int QuietSeconds { get; init; } = 180;

    public int PollSeconds { get; init; } = 60;
}

/// <summary>
/// The background memory pass (PLAN.md §6.6): once a conversation goes quiet, the model reads what's
/// new in it and picks out durable facts about the user, which are stored unless already known.
/// </summary>
/// <remarks>
/// The prompt was tried against familiar-chat before it went in (2026-10-02): from a conversation
/// about night shifts, a daughter and tea it kept "They are a nurse", "They have a daughter named
/// Maya", "They prefer their tea without sugar", and skipped the timer request in the middle; small
/// talk gave nothing, and facts already listed weren't repeated.
/// </remarks>
public sealed class MemoryExtractor(
    IDbContextFactory<FamiliarDbContext> db,
    ILlmClient llm,
    MemoryStore memories,
    MemoryOptions options,
    TimeProvider clock,
    ILogger<MemoryExtractor> logger) : BackgroundService
{
    internal const string Instructions = """
        You keep the long-term memory of a personal companion. Read the conversation and pick out
        facts about the user that will still be true and useful in weeks or months: their name, the
        people and pets in their life, their likes and dislikes, routines, work, health matters they
        mention, and plans or dates that matter to them.

        Leave out: small talk, passing moods, anything only relevant today, requests made to the
        companion (timers, reminders, questions), anything the companion said about itself, and
        anything already remembered (listed first).

        Write each fact as one short sentence about the user, in the third person: "Their daughter
        is called Maya." Write dates as dates ("on 9 October"), not "on Friday": the conversation's
        date is given. Only include what the user actually said; never guess. If nothing is worth
        remembering, return an empty list.
        """;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["facts"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("facts"),
    };

    /// <summary>The most a single pass reads; a longer conversation is read in several.</summary>
    private const int MaxMessages = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ExtractFromConversations)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (LlmUnavailableException exception)
            {
                // The model is resting; the conversations will still be there when it's back.
                logger.ModelUnavailable(exception.Message);
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            catch (Exception exception)
            {
                logger.MemoryPassFailed(exception);
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Reads every quiet conversation with something new in it. Returns how many facts were stored.</summary>
    internal async Task<int> PassAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var quietSince = now - TimeSpan.FromSeconds(options.QuietSeconds);

        List<ConversationRecord> due;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            due = await context.Conversations.AsNoTracking()
                .Where(c => c.LastMessageAt <= quietSince
                    && c.LastMessageAt > now.AddDays(-7)
                    && (c.MemoryCheckedAt == null || c.MemoryCheckedAt < c.LastMessageAt))
                .OrderBy(c => c.LastMessageAt)
                .Take(20)
                .ToListAsync(ct);
        }

        var stored = 0;
        foreach (var conversation in due)
        {
            stored += await ReadAsync(conversation, ct);
        }

        return stored;
    }

    private async Task<int> ReadAsync(ConversationRecord conversation, CancellationToken ct)
    {
        List<Message> messages;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            var after = conversation.MemoryCheckedAt ?? DateTimeOffset.MinValue;
            messages = await context.Messages.AsNoTracking()
                .Where(m => m.ConversationId == conversation.Id && m.CreatedAt > after)
                .OrderBy(m => m.CreatedAt)
                .Take(MaxMessages)
                .ToListAsync(ct);
        }

        if (messages.Count == 0)
        {
            await MarkAsync(conversation.Id, conversation.LastMessageAt, ct);
            return 0;
        }

        var stored = 0;

        // Nothing to learn from a stretch where the user said nothing (announcements, greetings).
        if (messages.Any(m => m.Role == MessageRole.User))
        {
            var known = await memories.ListAsync(conversation.AccountId, ct);
            // The date in UTC: conversations don't keep the user's zone, and a day out near midnight
            // is the worst that does.
            var answer = await llm.CompleteJsonAsync(Prompt(known, messages, TimeZoneInfo.Utc), Schema, ct);

            foreach (var fact in (answer["facts"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).OfType<string>().Take(10))
            {
                if (await memories.AddAsync(conversation.AccountId, fact, MemorySource.Conversation, messages[^1].Id, ct) is not null)
                {
                    stored++;
                }
            }

            logger.MemoriesExtracted(conversation.AccountId, messages.Count, stored);
        }

        await MarkAsync(conversation.Id, messages[^1].CreatedAt, ct);
        return stored;
    }

    internal static IReadOnlyList<ChatMessage> Prompt(IReadOnlyList<MemoryFact> known, IReadOnlyList<Message> messages, TimeZoneInfo zone)
    {
        var text = new StringBuilder("Already remembered:\n");
        if (known.Count == 0)
        {
            text.Append("(nothing yet)\n");
        }

        foreach (var memory in known)
        {
            text.Append("- ").Append(memory.Text).Append('\n');
        }

        var date = TimeZoneInfo.ConvertTime(messages[0].CreatedAt, zone);
        text.Append(CultureInfo.InvariantCulture, $"\nConversation ({date:dddd d MMMM yyyy}):\n");
        foreach (var message in messages)
        {
            text.Append(message.Role == MessageRole.User ? "User: " : "Companion: ").Append(message.Content).Append('\n');
        }

        return [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, text.ToString())];
    }

    private async Task MarkAsync(Guid conversationId, DateTimeOffset upTo, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await context.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.MemoryCheckedAt, upTo), ct);
    }
}
