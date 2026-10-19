using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Familiar.Server.Devices;

/// <summary>
/// Texts read back to the user and waiting for their yes (BACKLOG item 39): a phone that can send
/// texts itself sends one only once the user has heard who it's to and what it says, and said yes in
/// their very next message - checked here, in code, not left to the model. The yes sends what was
/// read back, whatever the model asks for then. One per account; in memory, since one waiting
/// across a restart is long past its moment.
/// </summary>
public sealed partial class TextDrafts(TimeProvider clock)
{
    /// <summary>How long a read-back text waits for its yes.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<Guid, TextDraft> _drafts = new();

    /// <summary>A text has been read back: it waits for the next thing the user says.</summary>
    public void Hold(Guid accountId, string to, string name, string message) =>
        _drafts[accountId] = new TextDraft(to, name, message, clock.GetUtcNow(), null);

    /// <summary>
    /// The user said something (<paramref name="said"/>, the turn <paramref name="replyId"/> answers):
    /// a plain yes approves the text waiting for that turn alone; anything else drops it. Null for
    /// words that aren't the user's own (a picture's).
    /// </summary>
    public void Heard(Guid accountId, Guid replyId, string? said)
    {
        if (!_drafts.TryRemove(accountId, out var draft))
        {
            return;
        }

        if (draft.ApprovedFor is null && said is not null && IsYes(said) && clock.GetUtcNow() - draft.At <= Wait)
        {
            _drafts[accountId] = draft with { ApprovedFor = replyId };
        }
    }

    /// <summary>The text the user said yes to, if it's this turn they said it in.</summary>
    public TextDraft? Approved(Guid accountId, Guid replyId) =>
        _drafts.TryGetValue(accountId, out var draft) && draft.ApprovedFor == replyId ? draft : null;

    /// <summary>Sent (or given up): nothing waits any more.</summary>
    public void Done(Guid accountId) => _drafts.TryRemove(accountId, out _);

    /// <summary>
    /// "Yes", "yeah, send it", "OK please", "go ahead": a short yes and nothing else. "Yes, but say
    /// I'm ten minutes late" isn't - it's a change, read back again.
    /// </summary>
    internal static bool IsYes(string said) => Yes().IsMatch(said.Trim());

    [GeneratedRegex(@"^(yes|yeah|yep|yup|yea|sure|ok|okay|alright|all right|correct|right|perfect|go ahead|go for it|send it|send|do it|please do|please|that's right|that's it|sounds good)([\s,!.]+(yes|yeah|please|send it|send|go ahead|do it|thanks|thank you|that's right))*[\s!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Yes();
}

/// <summary>A text read back and waiting.</summary>
/// <param name="To">Who, as the user said it - asked of the phone again to send.</param>
/// <param name="Name">The contact the phone found: what was read back.</param>
/// <param name="Message">The words read back: exactly what's sent.</param>
/// <param name="At">When it was read back.</param>
/// <param name="ApprovedFor">The turn the user said yes in, once they have.</param>
public sealed record TextDraft(string To, string Name, string Message, DateTimeOffset At, Guid? ApprovedFor);
