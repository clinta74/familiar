using System.ComponentModel.DataAnnotations;
using Familiar.Domain.Accounts;
using Familiar.Persistence;
using Familiar.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Auth;

/// <param name="Id">The invite.</param>
/// <param name="Note">Who it's for, as the admin put it.</param>
/// <param name="Status">waiting (can still be used), used, expired or cancelled.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="ExpiresAt">When it stops working, if it hasn't been used.</param>
/// <param name="CreatedBy">The admin who made it, if their account is still here.</param>
/// <param name="UsedAt">When someone registered with it.</param>
/// <param name="UsedBy">Who registered with it.</param>
/// <param name="CancelledAt">When an admin cancelled it.</param>
public sealed record InviteView(
    Guid Id,
    string? Note,
    [property: AllowedValues(Invites.Waiting, Invites.Used, Invites.Expired, Invites.Cancelled)] string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? CreatedBy,
    DateTimeOffset? UsedAt,
    string? UsedBy,
    DateTimeOffset? CancelledAt);

/// <param name="Note">Who it's for; optional, up to 80 characters.</param>
/// <param name="Hours">How long it works for: 1 hour to 30 days (720); 3 days if left out.</param>
public sealed record CreateInviteRequest(string? Note, int? Hours);

/// <param name="Invite">The invite made.</param>
/// <param name="Code">Its code, shown this once: the server keeps only a hash of it.</param>
/// <param name="Link">The link to send, if the server knows its public address (Auth:PublicUrl); otherwise the client makes it from its own.</param>
public sealed record NewInviteView(InviteView Invite, string Code, string? Link);

/// <param name="Open">Anyone may register; otherwise only with an invite.</param>
public sealed record RegistrationView(bool Open);

/// <param name="Open">Anyone may register (or nobody has yet: the first account is the admin).</param>
/// <param name="Invite">What the invite asked about says, when one was.</param>
public sealed record RegistrationStatusView(bool Open, InviteCheckView? Invite);

/// <param name="Valid">It can be used to register now.</param>
/// <param name="Problem">Why not, to show: used, expired, cancelled or not an invite.</param>
/// <param name="ExpiresAt">When it stops working, when it's valid.</param>
public sealed record InviteCheckView(bool Valid, string? Problem, DateTimeOffset? ExpiresAt);

/// <summary>
/// Invite-only registration: single-use links an admin makes, and the switch between letting anyone
/// register and only those with an invite. The switch is kept in the database, so an admin can
/// change it while the server runs; until they do, <see cref="AuthOptions.AllowRegistration"/> says.
/// </summary>
public static class Invites
{
    public const string Waiting = "waiting";
    public const string Used = "used";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    /// <summary>The code's prefix: an invite code is recognisable for what it is.</summary>
    public const string Prefix = "finv_";

    public const int DefaultHours = 72;
    public const int MaxHours = 30 * 24;
    public const int MaxNote = 80;

    private const string OpenSetting = "registration.open";

    /// <summary>Whether anyone may register: the admin's choice, or the configuration's until they've made one.</summary>
    public static async Task<bool> OpenAsync(FamiliarDbContext db, AuthOptions options, CancellationToken ct) =>
        await db.ServerSettings.AsNoTracking().Where(s => s.Name == OpenSetting).Select(s => s.Value).FirstOrDefaultAsync(ct) is { } value
            ? value == "true"
            : options.AllowRegistration;

    public static string StatusOf(Invite invite, DateTimeOffset now) =>
        invite.CancelledAt is not null ? Cancelled
        : invite.UsedAt is not null ? Used
        : invite.ExpiresAt <= now ? Expired
        : Waiting;

    /// <summary>
    /// The invite a code is for, tracked, and why it can't be used if it can't: null problem means
    /// it's waiting and the code's secret matches.
    /// </summary>
    public static async Task<(Invite? Invite, string? Problem)> FindAsync(FamiliarDbContext db, string code, DateTimeOffset now, CancellationToken ct)
    {
        if (!AccessTokenSecret.TryParse(code.Trim(), Prefix, out var id, out var secret)
            || await db.Invites.FirstOrDefaultAsync(i => i.Id == id, ct) is not { } invite
            || !AccessTokenSecret.Matches(secret, invite.SecretHash))
        {
            return (null, "That invite link isn't right: check it was copied whole, or ask for a new one.");
        }

        return StatusOf(invite, now) switch
        {
            Used => (invite, "That invite has been used already: each one is for one person. Ask for a new one."),
            Expired => (invite, "That invite has expired. Ask for a new one."),
            Cancelled => (invite, "That invite was cancelled. Ask for a new one."),
            _ => (invite, null),
        };
    }

    public static void MapInviteEndpoints(this IEndpointRouteBuilder routes)
    {
        // Anyone: whether they can register, and whether their invite is good, before they fill in the form.
        routes.MapGet("/api/auth/registration", StatusAsync).RequireRateLimiting(Infrastructure.RateLimiting.Auth);

        // Admins, from a browser (as the rest of user administration).
        var admin = routes.MapGroup("/api/admin")
            .RequireAuthorization(Policies.Admin, Policies.Browser)
            .AddEndpointFilter(async (context, next) =>
                await AdminEndpoints.CallerIsAdminAsync(context.HttpContext) ? await next(context) : Results.Forbid());
        admin.MapGet("/registration", async (FamiliarDbContext db, AuthOptions options, CancellationToken ct) =>
            new RegistrationView(await OpenAsync(db, options, ct)));
        admin.MapPut("/registration", SetOpenAsync);
        admin.MapGet("/invites", ListAsync);
        admin.MapPost("/invites", CreateAsync);
        admin.MapDelete("/invites/{id:guid}", CancelAsync);
    }

    private static async Task<RegistrationStatusView> StatusAsync(
        string? invite, FamiliarDbContext db, AuthOptions options, TimeProvider clock, CancellationToken ct)
    {
        var open = !await db.Accounts.AnyAsync(ct) || await OpenAsync(db, options, ct);
        if (string.IsNullOrWhiteSpace(invite))
        {
            return new RegistrationStatusView(open, null);
        }

        var (found, problem) = await FindAsync(db, invite, clock.GetUtcNow(), ct);
        return new RegistrationStatusView(open, new InviteCheckView(problem is null, problem, problem is null ? found!.ExpiresAt : null));
    }

    private static async Task<RegistrationView> SetOpenAsync(RegistrationView request, FamiliarDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var value = request.Open ? "true" : "false";
        var setting = await db.ServerSettings.FirstOrDefaultAsync(s => s.Name == OpenSetting, ct);
        if (setting is null)
        {
            db.ServerSettings.Add(new ServerSetting { Name = OpenSetting, Value = value, UpdatedAt = clock.GetUtcNow() });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        return new RegistrationView(request.Open);
    }

    private static async Task<List<InviteView>> ListAsync(FamiliarDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var invites = await db.Invites.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(ct);
        var makers = invites.Select(i => i.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => makers.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Username, ct);
        return [.. invites.Select(i => View(i, now, i.CreatedBy is { } by ? names.GetValueOrDefault(by) : null))];
    }

    private static async Task<IResult> CreateAsync(
        CreateInviteRequest request, HttpContext http, FamiliarDbContext db, AuthOptions options, TimeProvider clock, CancellationToken ct)
    {
        var hours = request.Hours ?? DefaultHours;
        if (hours is < 1 or > MaxHours)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "An invite can last from an hour to 30 days.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > MaxNote)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Keep the note to {MaxNote} characters.");
        }

        var id = Guid.CreateVersion7();
        var (code, hash) = AccessTokenSecret.Mint(id, Prefix);
        var now = clock.GetUtcNow();
        var invite = new Invite
        {
            Id = id,
            SecretHash = hash,
            Note = note,
            CreatedBy = http.User.AccountId(),
            CreatedAt = now,
            ExpiresAt = now.AddHours(hours),
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);

        var maker = await db.Accounts.Where(a => a.Id == invite.CreatedBy).Select(a => a.Username).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new NewInviteView(View(invite, now, maker), code, LinkFor(options, code)));
    }

    private static async Task<IResult> CancelAsync(Guid id, FamiliarDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (await db.Invites.FirstOrDefaultAsync(i => i.Id == id, ct) is not { } invite)
        {
            return Results.NotFound();
        }

        if (StatusOf(invite, now) != Waiting)
        {
            return AuthEndpoints.Error(StatusCodes.Status409Conflict, "Only an invite that's still waiting can be cancelled.");
        }

        invite.CancelledAt = now;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>The link to register with: on the public address, when the server's been told it.</summary>
    internal static string? LinkFor(AuthOptions options, string code) =>
        Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var root) && (root.Scheme == Uri.UriSchemeHttps || root.Scheme == Uri.UriSchemeHttp)
            ? $"{root.GetLeftPart(UriPartial.Path).TrimEnd('/')}/register?invite={Uri.EscapeDataString(code)}"
            : null;

    private static InviteView View(Invite invite, DateTimeOffset now, string? maker) => new(
        invite.Id, invite.Note, StatusOf(invite, now), invite.CreatedAt, invite.ExpiresAt, maker,
        invite.UsedAt, invite.UsedByUsername, invite.CancelledAt);
}
