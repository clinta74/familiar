using Familiar.Agent.Llm;
using Familiar.Agent.Prompting;
using Familiar.Agent.Speech;
using Familiar.Agent.Tools;
using Familiar.Agent.Turns;
using Familiar.Domain.Conversations;
using Familiar.Domain.Expression;
using Familiar.Domain.Scheduling;
using Familiar.Server.Awareness;
using Familiar.Server.Tools;

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
    IToolExecutor tools,
    SituationProvider situations,
    AwarenessOptions awareness,
    Hooks.WebhookDispatcher webhooks,
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
            try
            {
                text = await scope.ServiceProvider.GetRequiredService<ISpeechToText>().TranscribeAsync(audio, contentType, ct);
            }
            catch (SpeechUnavailableException exception)
            {
                // The user is told only "I can't hear right now"; why (unreachable, timed out, no
                // model installed) is for the log. And it's no longer thinking about anything.
                logger.SpeechUnavailable(exception.Message);
                channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
                throw;
            }
        }

        var transcribeMs = (long)clock.GetElapsedTime(receivedAt).TotalMilliseconds;

        if (text.Length == 0)
        {
            channel.Publish(EventTypes.Error, new ErrorEvent("no-speech", "I didn't catch that - could you say it again?"));
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
            return null;
        }

        // The familiar hearing its own voice through the speakers (hands-free keeps the mic open).
        if (EchoDetector.IsEcho(text, await store.LastReplyAsync(accountId, ct)))
        {
            logger.EchoIgnored(text);
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
            return null;
        }

        return await SubmitAsync(accountId, text, new TurnRequest(zone, Speak: true, receivedAt, transcribeMs), ct);
    }

    /// <summary>
    /// Stops the reply in progress, if there is one, and waits until it has been stored. With
    /// <paramref name="heard"/>, the reply is then cut to what the user actually heard of it: the model
    /// usually finishes writing long before the familiar finishes speaking, so the stored reply would
    /// otherwise "remember" saying things the user never heard.
    /// </summary>
    public async Task InterruptAsync(Guid accountId, Guid? replyId = null, string? heard = null)
    {
        var channel = hub.For(accountId);
        await channel.TurnGate.WaitAsync();
        try
        {
            await InterruptLockedAsync(channel);

            if (replyId is { } id && heard is not null
                && await store.TruncateReplyAsync(accountId, id, heard, CancellationToken.None) is { } cut)
            {
                channel.Publish(EventTypes.ReplyTruncated, new ReplyTruncatedEvent(cut.Id, cut.Content));
            }
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

    /// <summary>
    /// A timer or reminder went off: the familiar says so, as a reply of its own (PLAN.md §6.2).
    /// </summary>
    public Task AnnounceAsync(ScheduledJob job, string text, CancellationToken ct)
    {
        var late = clock.GetUtcNow() - job.DueAt > TimeSpan.FromMinutes(1);
        var (emotion, intensity, gesture) = (late, job.Kind) switch
        {
            (true, _) => (Emotion.Sad, 0.4, Gesture.Shrug),
            (_, JobKind.Timer) => (Emotion.Joy, 0.7, Gesture.Excited),
            _ => (Emotion.Relaxed, 0.6, Gesture.Nod),
        };

        var kind = job.Kind == JobKind.Timer ? NotifyKinds.Timer : NotifyKinds.Reminder;
        return AnnounceAsync(new Announcement(job.AccountId, text, emotion, intensity, gesture, new NotifyEvent(job.Id, kind, job.Label, text)), ct);
    }

    /// <summary>
    /// The familiar says something unprompted, word for word: an announcement. It waits for a reply
    /// in progress to finish rather than talking over it, and like any reply it can be interrupted.
    /// </summary>
    public async Task AnnounceAsync(Announcement announcement, CancellationToken ct)
    {
        var channel = hub.For(announcement.AccountId);
        await QueueAsync(channel, token => SayAsync(channel, announcement, token), ct);
    }

    /// <summary>
    /// The familiar says something unprompted in its own words: the model gets <paramref name="cue"/>
    /// in place of a user message, and no tools. For "tell them…" from a hook.
    /// </summary>
    public async Task TellAsync(Guid accountId, string cue, TimeZoneInfo zone, bool speak, CancellationToken ct)
    {
        var channel = hub.For(accountId);
        await QueueAsync(channel, token => RunAsync(channel, accountId, null, new TurnRequest(zone, speak, clock.GetTimestamp()), token, cue), ct);
    }

    /// <summary>Runs something as the account's next turn, once the one in progress (if any) is done.</summary>
    private async Task QueueAsync(AccountChannel channel, Func<CancellationToken, Task> turn, CancellationToken ct)
    {
        await channel.TurnGate.WaitAsync(ct);
        try
        {
            if (channel.TurnTask is { } running)
            {
                await running;
            }

            channel.TurnCancellation?.Dispose();

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            channel.TurnCancellation = cancellation;
            channel.TurnTask = Task.Run(() => turn(cancellation.Token), CancellationToken.None);
        }
        finally
        {
            channel.TurnGate.Release();
        }
    }

    private async Task SayAsync(AccountChannel channel, Announcement announcement, CancellationToken ct)
    {
        var replyId = Guid.CreateVersion7();
        VoicePipeline? voice = null;
        await using var scope = scopes.CreateAsyncScope();
        var (accountId, text, emotion, intensity, gesture, notify) = announcement;

        try
        {
            var profile = await store.ProfileAsync(accountId, ct);
            var conversation = await store.CurrentAsync(accountId, ct);

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

            channel.Publish(EventTypes.Notify, notify);
            channel.Publish(EventTypes.Expression, new ExpressionEvent(emotion, Math.Round(intensity * profile.Expressiveness, 2)));
            channel.Publish(EventTypes.ReplyStart, new ReplyStartEvent(replyId));
            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Speaking));
            if (gesture is { } g)
            {
                channel.Publish(EventTypes.Gesture, new GestureEvent(g));
            }

            channel.Publish(EventTypes.ReplyDelta, new ReplyDeltaEvent(replyId, text));
            voice.Add(text);

            await store.AddAsync(new Message
            {
                Id = replyId,
                ConversationId = conversation.Id,
                Role = MessageRole.Assistant,
                Content = text,
                Emotion = emotion,
                EmotionIntensity = intensity,
                CreatedAt = clock.GetUtcNow(),
            }, CancellationToken.None);

            channel.Publish(EventTypes.ReplyEnd, new ReplyEndEvent(replyId, text, emotion, intensity, Interrupted: false));
            await voice.CompleteAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Talked over, or the server is stopping.
        }
        catch (Exception exception)
        {
            logger.AnnounceFailed(exception, notify.Id);
        }
        finally
        {
            if (voice is not null)
            {
                await voice.CompleteAsync();
            }

            channel.Publish(EventTypes.State, new StateEvent(FamiliarState.Idle));
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

    /// <summary>
    /// The familiar speaks first (PLAN.md §6.3): when someone comes back after a while, it says hello
    /// and mentions anything coming up soon. At most once per <see cref="GreetingGap"/> however many
    /// devices connect, never over a turn in progress, and not at all if the user switched it off.
    /// </summary>
    public async Task GreetAsync(Guid accountId, TimeZoneInfo zone, bool speak, CancellationToken ct)
    {
        // Called without awaiting, from the stream: failures end here, logged.
        try
        {
            await TryGreetAsync(accountId, zone, speak, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.GreetingFailed(exception, accountId);
        }
    }

    private async Task TryGreetAsync(Guid accountId, TimeZoneInfo zone, bool speak, CancellationToken ct)
    {
        if (!awareness.Greet)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var last = await store.LastMessageAtAsync(accountId, now, ct);
        if (last is { } at && now - at < GreetingGap)
        {
            // Mid-conversation: a greeting would be odd.
            return;
        }

        var channel = hub.For(accountId);
        if (!await channel.TurnGate.WaitAsync(TimeSpan.Zero, ct))
        {
            return;
        }

        try
        {
            if (channel.TurnTask is { IsCompleted: false } || !await store.TryClaimGreetingAsync(accountId, now, GreetingGap, ct))
            {
                return;
            }

            channel.TurnCancellation?.Dispose();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            channel.TurnCancellation = cancellation;
            channel.TurnTask = Task.Run(
                () => RunAsync(channel, accountId, null, new TurnRequest(zone, speak, clock.GetTimestamp()), cancellation.Token, PromptBuilder.GreetingCue),
                CancellationToken.None);
        }
        finally
        {
            channel.TurnGate.Release();
        }
    }

    /// <summary>How long a quiet spell must be before the familiar greets, and how often it may.</summary>
    internal static readonly TimeSpan GreetingGap = TimeSpan.FromHours(3);

    /// <summary>
    /// One reply. With no <paramref name="userMessage"/>, the familiar speaks first, prompted by
    /// <paramref name="cue"/> (a greeting, or something a hook asked it to pass on).
    /// </summary>
    private async Task RunAsync(
        AccountChannel channel, Guid accountId, Message? userMessage, TurnRequest request, CancellationToken ct, string? cue = null)
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
            var now = clock.GetUtcNow();
            var conversationId = userMessage?.ConversationId ?? (await store.CurrentAsync(accountId, ct)).Id;
            var history = await store.HistoryAsync(conversationId, userMessage?.Id ?? Guid.Empty, llmOptions.HistoryMessages, ct);
            var situation = await situations.ForAsync(accountId, request.Zone, now, userMessage?.CreatedAt ?? now, ct);
            var context = new PromptContext(
                profile, username, history, userMessage?.Content ?? "", now, request.Zone, situation.Notes, situation.Memories,
                Familiars.AppearanceView.Parse(profile.Appearance).Look());
            var prompt = userMessage is null ? PromptBuilder.BuildUnprompted(context, cue ?? PromptBuilder.GreetingCue) : PromptBuilder.Build(context);

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

                    case SoundMade sound:
                        channel.Publish(EventTypes.Sound, new SoundEvent(sound.Sound));
                        break;

                    case TagIgnored ignored:
                        logger.TagIgnored(ignored.Tag);
                        break;

                    case ToolStarted tool:
                        channel.Publish(EventTypes.ToolStarted, new ToolStartedEvent(replyId, tool.CallId, tool.Tool));
                        break;

                    case ToolFinished tool:
                        channel.Publish(EventTypes.ToolFinished, new ToolFinishedEvent(replyId, tool.CallId, tool.Tool, tool.Ok, tool.Summary));
                        break;
                }

                return ValueTask.CompletedTask;
            }

            var runner = scope.ServiceProvider.GetRequiredService<TurnRunner>();
            var toolContext = new ToolContext(accountId, replyId, request.Zone, clock.GetUtcNow());
            // A greeting gets no tools: it's a hello, not a chance to start doing things.
            var result = userMessage is null
                ? await runner.RunAsync(prompt, Emit, ct)
                : await runner.RunAsync(prompt, Emit, ct, tools, toolContext);

            if (started || result.Text.Length > 0)
            {
                // CancellationToken.None: an interrupted reply is stored all the same.
                await store.AddAsync(new Message
                {
                    Id = replyId,
                    ConversationId = conversationId,
                    Role = MessageRole.Assistant,
                    Content = result.Text,
                    Emotion = result.Emotion,
                    EmotionIntensity = result.Intensity,
                    Interrupted = result.Interrupted,
                    CreatedAt = clock.GetUtcNow(),
                }, CancellationToken.None);

                channel.Publish(EventTypes.ReplyEnd,
                    new ReplyEndEvent(replyId, result.Text, result.Emotion, result.Intensity, result.Interrupted));
                webhooks.Raise(accountId, Hooks.WebhookEvents.Reply, new
                {
                    id = replyId,
                    text = result.Text,
                    emotion = result.Emotion.ToString().ToLowerInvariant(),
                    interrupted = result.Interrupted,
                    unprompted = userMessage is null,
                });
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
