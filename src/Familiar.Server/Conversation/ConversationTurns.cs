using Familiar.Agent.Llm;
using Familiar.Agent.Prompting;
using Familiar.Agent.Turns;
using Familiar.Domain.Conversations;

namespace Familiar.Server.Conversation;

/// <summary>
/// Starts, runs and interrupts turns. One turn at a time per account: a new message interrupts the
/// reply in progress, the way talking over someone does (PLAN.md §3.2).
/// </summary>
/// <remarks>
/// A turn runs in the background, not inside the POST that started it. The POST returns as soon as
/// the message is stored; everything after arrives on the event stream, on every device.
/// </remarks>
public sealed class ConversationTurns(
    ConversationHub hub,
    ConversationStore store,
    IServiceScopeFactory scopes,
    LlmOptions llmOptions,
    TimeProvider clock,
    IHostApplicationLifetime lifetime,
    ILogger<ConversationTurns> logger)
{
    public async Task<Message> SubmitAsync(Guid accountId, string text, TimeZoneInfo zone, CancellationToken ct)
    {
        var channel = hub.For(accountId);
        await channel.TurnGate.WaitAsync(ct);
        try
        {
            await InterruptLockedAsync(channel);

            var conversation = await store.CurrentAsync(accountId, ct);
            var message = await store.AddAsync(new Message
            {
                ConversationId = conversation.Id,
                Role = MessageRole.User,
                Content = text,
                CreatedAt = clock.GetUtcNow(),
            }, ct);

            channel.Publish(EventTypes.Message, MessageView.From(message));

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            channel.TurnCancellation = cancellation;
            channel.TurnTask = Task.Run(() => RunAsync(channel, accountId, message, zone, cancellation.Token), CancellationToken.None);

            return message;
        }
        finally
        {
            channel.TurnGate.Release();
        }
    }

    /// <summary>Stops the reply in progress, if there is one, and waits until it has been stored.</summary>
    public async Task InterruptAsync(Guid accountId)
    {
        var channel = hub.For(accountId);
        await channel.TurnGate.WaitAsync();
        try
        {
            await InterruptLockedAsync(channel);
        }
        finally
        {
            channel.TurnGate.Release();
        }
    }

    /// <summary>Ends the current conversation; the next message starts a fresh one.</summary>
    public async Task StartOverAsync(Guid accountId, CancellationToken ct)
    {
        var channel = hub.For(accountId);
        await channel.TurnGate.WaitAsync(ct);
        try
        {
            await InterruptLockedAsync(channel);
            await store.StartAsync(accountId, ct);
            channel.Publish(EventTypes.Cleared, new EmptyEvent());
        }
        finally
        {
            channel.TurnGate.Release();
        }
    }

    private static async Task InterruptLockedAsync(AccountChannel channel)
    {
        if (channel.TurnCancellation is not { } cancellation)
        {
            return;
        }

        await cancellation.CancelAsync();
        if (channel.TurnTask is { } task)
        {
            // RunAsync handles its own failures; this only waits for the partial reply to be stored.
            await task;
        }

        cancellation.Dispose();
        channel.TurnCancellation = null;
        channel.TurnTask = null;
    }

    private async Task RunAsync(AccountChannel channel, Guid accountId, Message userMessage, TimeZoneInfo zone, CancellationToken ct)
    {
        var replyId = Guid.CreateVersion7();
        var started = false;

        try
        {
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Thinking));

            var profile = await store.ProfileAsync(accountId, ct);
            var username = await store.UsernameAsync(accountId, ct);
            var history = await store.HistoryAsync(userMessage.ConversationId, userMessage.Id, llmOptions.HistoryMessages, ct);
            var prompt = PromptBuilder.Build(new PromptContext(profile, username, history, userMessage.Content, clock.GetUtcNow(), zone));

            ValueTask Emit(TurnEvent turnEvent)
            {
                switch (turnEvent)
                {
                    case SpeechDelta delta:
                        if (!started)
                        {
                            started = true;
                            channel.Publish(EventTypes.ReplyStart, new ReplyStartEvent(replyId));
                            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Speaking));
                        }

                        channel.Publish(EventTypes.ReplyDelta, new ReplyDeltaEvent(replyId, delta.Text));
                        break;

                    case ExpressionChanged expression:
                        // The user's expressiveness setting scales what the avatar shows, not what's stored.
                        channel.Publish(EventTypes.Expression,
                            new ExpressionEvent(expression.Emotion, Math.Round(expression.Intensity * profile.Expressiveness, 2)));
                        break;

                    case GestureMade gesture:
                        channel.Publish(EventTypes.Gesture, new GestureEvent(gesture.Gesture));
                        break;
                }

                return ValueTask.CompletedTask;
            }

            await using var scope = scopes.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<TurnRunner>();
            var result = await runner.RunAsync(prompt, Emit, ct);

            if (started || result.Text.Length > 0)
            {
                // CancellationToken.None: an interrupted reply is stored all the same.
                await store.AddAsync(new Message
                {
                    Id = replyId,
                    ConversationId = userMessage.ConversationId,
                    Role = MessageRole.Assistant,
                    Content = result.Text,
                    Emotion = result.Emotion,
                    EmotionIntensity = result.Intensity,
                    Interrupted = result.Interrupted,
                    CreatedAt = clock.GetUtcNow(),
                }, CancellationToken.None);

                channel.Publish(EventTypes.ReplyEnd,
                    new ReplyEndEvent(replyId, result.Text, result.Emotion, result.Intensity, result.Interrupted));
            }

            if (!result.EmotionWasTagged && !result.Interrupted)
            {
                logger.ReplyUntagged(accountId);
            }

            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
        }
        catch (LlmUnavailableException exception)
        {
            logger.ModelUnavailable(exception.Message);
            channel.Publish(EventTypes.Error, new ErrorEvent("resting", "I'm resting right now - my thinking space isn't reachable. Try again in a moment."));
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Resting));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Interrupted before the model said anything (still loading history, or the server is
            // stopping). Nothing to store.
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
        }
        catch (Exception exception)
        {
            logger.TurnFailed(exception, accountId);
            channel.Publish(EventTypes.Error, new ErrorEvent("failed", "Something went wrong on my side. Please try again."));
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
        }
    }
}
