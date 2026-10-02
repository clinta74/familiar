using Familiar.Agent.Llm;
using Familiar.Agent.Prompting;
using Familiar.Agent.Speech;
using Familiar.Agent.Turns;
using Familiar.Domain.Conversations;

namespace Familiar.Server.Conversation;

/// <summary>How a message arrived, and whether its reply should be spoken.</summary>
/// <param name="Zone">The user's time zone.</param>
/// <param name="Speak">Synthesise the reply (PLAN.md §3.2).</param>
/// <param name="ReceivedAt">When the user finished speaking or typing - the start of the latency clock.</param>
/// <param name="TranscribeMs">For a spoken message, how long speech-to-text took.</param>
public sealed record TurnRequest(TimeZoneInfo Zone, bool Speak, long ReceivedAt, long? TranscribeMs = null);

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
    SpeechClipStore clips,
    IServiceScopeFactory scopes,
    LlmOptions llmOptions,
    SpeechOptions speechOptions,
    TimeProvider clock,
    IHostApplicationLifetime lifetime,
    ILogger<ConversationTurns> logger)
{
    public async Task<Message> SubmitAsync(Guid accountId, string text, TurnRequest request, CancellationToken ct)
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
            channel.TurnTask = Task.Run(() => RunAsync(channel, accountId, message, request, cancellation.Token), CancellationToken.None);

            return message;
        }
        finally
        {
            channel.TurnGate.Release();
        }
    }

    /// <summary>
    /// A spoken message: transcribe it, then carry on as if it had been typed - except that the reply
    /// is always spoken. Null when there were no words in it.
    /// </summary>
    /// <exception cref="SpeechUnavailableException">Speech-to-text can't be used.</exception>
    public async Task<Message?> SubmitSpokenAsync(Guid accountId, byte[] audio, string contentType, TimeZoneInfo zone, CancellationToken ct)
    {
        var receivedAt = clock.GetTimestamp();
        var channel = hub.For(accountId);

        // Listening is over; the familiar is working on it. (Before the gate: a reply still being
        // spoken is interrupted by SubmitAsync, not made to wait for transcription.)
        channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Thinking));

        string text;
        await using (var scope = scopes.CreateAsyncScope())
        {
            text = await scope.ServiceProvider.GetRequiredService<ISpeechToText>().TranscribeAsync(audio, contentType, ct);
        }

        var transcribeMs = (long)clock.GetElapsedTime(receivedAt).TotalMilliseconds;

        if (text.Length == 0)
        {
            channel.Publish(EventTypes.Error, new ErrorEvent("no-speech", "I didn't catch that - could you say it again?"));
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
            return null;
        }

        return await SubmitAsync(accountId, text, new TurnRequest(zone, Speak: true, receivedAt, transcribeMs), ct);
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

    private async Task RunAsync(AccountChannel channel, Guid accountId, Message userMessage, TurnRequest request, CancellationToken ct)
    {
        var replyId = Guid.CreateVersion7();
        var started = false;
        long? firstTextMs = null;
        VoicePipeline? voice = null;

        // Outlives the try: the voice worker uses the scope's TTS client until it's completed in finally.
        await using var scope = scopes.CreateAsyncScope();

        try
        {
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Thinking));

            var profile = await store.ProfileAsync(accountId, ct);
            var username = await store.UsernameAsync(accountId, ct);
            var history = await store.HistoryAsync(userMessage.ConversationId, userMessage.Id, llmOptions.HistoryMessages, ct);
            var prompt = PromptBuilder.Build(new PromptContext(profile, username, history, userMessage.Content, clock.GetUtcNow(), request.Zone));

            if (request.Speak)
            {
                voice = new VoicePipeline(
                    scope.ServiceProvider.GetRequiredService<ITextToSpeech>(),
                    clips,
                    channel,
                    accountId,
                    replyId,
                    profile.Voice ?? speechOptions.DefaultVoice,
                    profile.VoiceSpeed,
                    clock,
                    logger,
                    ct);
            }

            ValueTask Emit(TurnEvent turnEvent)
            {
                switch (turnEvent)
                {
                    case SpeechDelta delta:
                        if (!started)
                        {
                            started = true;
                            firstTextMs = (long)clock.GetElapsedTime(request.ReceivedAt).TotalMilliseconds;
                            channel.Publish(EventTypes.ReplyStart, new ReplyStartEvent(replyId));
                            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Speaking));
                        }

                        channel.Publish(EventTypes.ReplyDelta, new ReplyDeltaEvent(replyId, delta.Text));
                        voice?.Add(delta.Text);
                        break;

                    case ExpressionChanged expression:
                        // The user's expressiveness setting scales what the avatar shows, not what's stored.
                        channel.Publish(EventTypes.Expression,
                            new ExpressionEvent(expression.Emotion, Math.Round(expression.Intensity * profile.Expressiveness, 2)));
                        break;

                    case GestureMade gesture:
                        channel.Publish(EventTypes.Gesture, new GestureEvent(gesture.Gesture));
                        break;

                    case TagIgnored ignored:
                        logger.TagIgnored(ignored.Tag);
                        break;
                }

                return ValueTask.CompletedTask;
            }

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

            if (voice is not null)
            {
                await voice.CompleteAsync();
            }

            if (!result.EmotionWasTagged && !result.Interrupted)
            {
                logger.ReplyUntagged(accountId);
            }

            logger.TurnTiming(
                request.TranscribeMs ?? -1,
                firstTextMs ?? -1,
                voice?.FirstAudioAt is { } firstAudio ? (long)clock.GetElapsedTime(request.ReceivedAt, firstAudio).TotalMilliseconds : -1,
                (long)clock.GetElapsedTime(request.ReceivedAt).TotalMilliseconds);

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
        finally
        {
            if (voice is not null)
            {
                await voice.CompleteAsync();
            }
        }
    }
}
