using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Shopping;
using Familiar.Domain.Tools;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Conversation;
using Familiar.Server.Tools;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Shopping;

/// <param name="Id">The item.</param>
/// <param name="Text">What to get.</param>
/// <param name="AddedAt">When it went on the list.</param>
/// <param name="GotAt">When it was ticked off; null while it's still to get.</param>
public sealed record ShoppingItemView(Guid Id, string Text, DateTimeOffset AddedAt, DateTimeOffset? GotAt)
{
    public static ShoppingItemView From(ShoppingItem item) => new(item.Id, item.Text, item.AddedAt, item.GotAt);
}

/// <summary>The shopping list as it is now, to every open tab and app, whenever it changes.</summary>
/// <param name="Items">Still to get first, oldest first; then what's been got.</param>
public sealed record ShoppingEvent(IReadOnlyList<ShoppingItemView> Items);

/// <param name="Text">What to get.</param>
public sealed record ShoppingItemRequest(string? Text);

/// <param name="Got">Ticked off, or not.</param>
public sealed record ShoppingTickRequest(bool Got);

/// <summary>
/// The account's shopping list: what the shopping_list tool, the website and the app all change, each
/// change sent to every open client as a <see cref="ShoppingEvent"/>.
/// </summary>
public sealed class ShoppingList(IDbContextFactory<FamiliarDbContext> db, ConversationHub hub, TimeProvider clock)
{
    public async Task<IReadOnlyList<ShoppingItem>> ListAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return Ordered(await context.ShoppingItems.AsNoTracking().Where(i => i.AccountId == accountId).ToListAsync(ct));
    }

    /// <summary>
    /// Puts things on the list: what's there already, still to get, isn't added twice; what was
    /// ticked off goes back to get.
    /// </summary>
    public async Task<(IReadOnlyList<string> Added, IReadOnlyList<string> Already)> AddAsync(Guid accountId, IEnumerable<string> texts, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var items = await context.ShoppingItems.Where(i => i.AccountId == accountId).ToListAsync(ct);
        var added = new List<string>();
        var already = new List<string>();
        foreach (var text in texts.Select(Clean).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (items.FirstOrDefault(i => Same(i.Text, text)) is { } existing)
            {
                if (existing.GotAt is null)
                {
                    already.Add(existing.Text);
                    continue;
                }

                existing.GotAt = null;
                added.Add(existing.Text);
                continue;
            }

            if (items.Count >= ShoppingItem.MaxPerAccount)
            {
                break;
            }

            var item = new ShoppingItem { AccountId = accountId, Text = text, AddedAt = clock.GetUtcNow() };
            context.ShoppingItems.Add(item);
            items.Add(item);
            added.Add(text);
        }

        await context.SaveChangesAsync(ct);
        await PublishAsync(accountId, ct);
        return (added, already);
    }

    /// <summary>Ticks off what's said ("the milk"), or puts it back: the items matched, and what matched nothing.</summary>
    public Task<(IReadOnlyList<string> Done, IReadOnlyList<string> NotFound)> TickAsync(Guid accountId, IEnumerable<string> said, bool got, CancellationToken ct) =>
        ChangeAsync(accountId, said, (context, item) => item.GotAt = got ? clock.GetUtcNow() : null, ct);

    /// <summary>Takes what's said off the list altogether.</summary>
    public Task<(IReadOnlyList<string> Done, IReadOnlyList<string> NotFound)> RemoveAsync(Guid accountId, IEnumerable<string> said, CancellationToken ct) =>
        ChangeAsync(accountId, said, (context, item) => context.ShoppingItems.Remove(item), ct);

    /// <summary>Empties the list - or only what's been ticked off.</summary>
    public async Task<int> ClearAsync(Guid accountId, bool everything, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var cleared = await context.ShoppingItems
            .Where(i => i.AccountId == accountId && (everything || i.GotAt != null))
            .ExecuteDeleteAsync(ct);
        await PublishAsync(accountId, ct);
        return cleared;
    }

    /// <summary>One item, ticked or not, by id (the website and the app).</summary>
    public async Task<ShoppingItem?> SetGotAsync(Guid accountId, Guid id, bool got, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.ShoppingItems.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId, ct) is not { } item)
        {
            return null;
        }

        item.GotAt = got ? item.GotAt ?? clock.GetUtcNow() : null;
        await context.SaveChangesAsync(ct);
        await PublishAsync(accountId, ct);
        return item;
    }

    public async Task<bool> DeleteAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var deleted = await context.ShoppingItems.Where(i => i.Id == id && i.AccountId == accountId).ExecuteDeleteAsync(ct) > 0;
        if (deleted)
        {
            await PublishAsync(accountId, ct);
        }

        return deleted;
    }

    /// <summary>
    /// The items <paramref name="said"/> means: the same thing ("Milk" for "milk", "battery" for
    /// "batteries"), or, failing that, the one item that contains it or that it contains ("the
    /// semi-skimmed milk" for "milk").
    /// </summary>
    internal static IReadOnlyList<ShoppingItem> Matching(IReadOnlyList<ShoppingItem> items, string said)
    {
        var same = items.Where(i => Same(i.Text, said)).ToList();
        if (same.Count > 0)
        {
            return same;
        }

        var key = Key(said);
        var near = items.Where(i => Key(i.Text).Contains(key, StringComparison.Ordinal) || key.Contains(Key(i.Text), StringComparison.Ordinal)).ToList();
        return near.Count == 1 ? near : [];
    }

    private async Task<(IReadOnlyList<string> Done, IReadOnlyList<string> NotFound)> ChangeAsync(
        Guid accountId, IEnumerable<string> said, Action<FamiliarDbContext, ShoppingItem> change, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var items = await context.ShoppingItems.Where(i => i.AccountId == accountId).ToListAsync(ct);
        var done = new List<string>();
        var notFound = new List<string>();
        foreach (var text in said.Select(Clean).Where(t => t.Length > 0))
        {
            var matched = Matching(items, text);
            if (matched.Count == 0)
            {
                notFound.Add(text);
                continue;
            }

            foreach (var item in matched)
            {
                change(context, item);
                done.Add(item.Text);
            }
        }

        await context.SaveChangesAsync(ct);
        if (done.Count > 0)
        {
            await PublishAsync(accountId, ct);
        }

        return (done, notFound);
    }

    private async Task PublishAsync(Guid accountId, CancellationToken ct)
    {
        var items = await ListAsync(accountId, ct);
        hub.For(accountId).Publish(EventTypes.Shopping, new ShoppingEvent([.. items.Select(ShoppingItemView.From)]));
    }

    private static List<ShoppingItem> Ordered(List<ShoppingItem> items) =>
        [.. items.Where(i => i.GotAt is null).OrderBy(i => i.AddedAt), .. items.Where(i => i.GotAt is not null).OrderBy(i => i.GotAt)];

    private static string Clean(string text)
    {
        var cleaned = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return cleaned.Length > ShoppingItem.MaxText ? cleaned[..ShoppingItem.MaxText] : cleaned;
    }

    private static bool Same(string a, string b) => Key(a) == Key(b);

    /// <summary>"Some AA Batteries" and "aa battery" alike: lower case, no "a"/"the"/"some", no plural s.</summary>
    private static string Key(string text)
    {
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] is "a" or "an" or "the" or "some" or "more")
        {
            words.RemoveAt(0);
        }

        var key = string.Join(' ', words);
        return key.EndsWith("ies", StringComparison.Ordinal) ? key[..^3] + "y"
            : key.EndsWith("es", StringComparison.Ordinal) && key.Length > 4 && key[^3] is 's' or 'x' or 'h' ? key[..^2]
            : key.EndsWith('s') && !key.EndsWith("ss", StringComparison.Ordinal) && key.Length > 3 ? key[..^1]
            : key;
    }
}

/// <summary>The shopping list, for the website and the app.</summary>
public static class ShoppingEndpoints
{
    public static void MapShoppingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/shopping").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, ShoppingList list, CancellationToken ct) =>
            (await list.ListAsync(http.User.AccountId()!.Value, ct)).Select(ShoppingItemView.From).ToList());

        group.MapPost("/", async (ShoppingItemRequest request, HttpContext http, ShoppingList list, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > ShoppingItem.MaxText)
            {
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"An item is 1-{ShoppingItem.MaxText} characters.");
            }

            var accountId = http.User.AccountId()!.Value;
            await list.AddAsync(accountId, [request.Text], ct);
            return Results.Ok((await list.ListAsync(accountId, ct)).Select(ShoppingItemView.From).ToList());
        });

        group.MapPut("/{id:guid}", async (Guid id, ShoppingTickRequest request, HttpContext http, ShoppingList list, CancellationToken ct) =>
            await list.SetGotAsync(http.User.AccountId()!.Value, id, request.Got, ct) is { } item
                ? Results.Ok(ShoppingItemView.From(item))
                : Results.NotFound());

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ShoppingList list, CancellationToken ct) =>
            await list.DeleteAsync(http.User.AccountId()!.Value, id, ct) ? Results.NoContent() : Results.NotFound());

        // ?everything=true empties the list; otherwise only what's been ticked off goes.
        group.MapDelete("/", async (bool? everything, HttpContext http, ShoppingList list, CancellationToken ct) =>
        {
            await list.ClearAsync(http.User.AccountId()!.Value, everything == true, ct);
            return Results.NoContent();
        });
    }
}

/// <summary>
/// shopping_list: the list by voice - "add milk and batteries", "what's on the shopping list?", "I
/// got the milk", "take the bread off", "clear the list". One tool rather than five: every tool's
/// description is in every prompt.
/// </summary>
public sealed class ShoppingListTool(ShoppingList list) : ITool, IRiskPerCall
{
    private static readonly string[] Actions = ["add", "show", "got", "remove", "clear"];

    public ToolDefinition Definition { get; } = new(
        "shopping_list",
        "The user's shopping list. add: put things on it. show: what's on it. got: tick things off, as they get them. " +
        "remove: take things off that they don't need after all. clear: empty it (only what's been got, unless they say " +
        "the whole list). One thing per item, as they said it.",
        ToolSchema.Object(
            ["action"],
            ("action", ToolSchema.Enum("What to do.", Actions)),
            ("items", ToolSchema.StringList("The things, one each: [\"milk\", \"AA batteries\"]. For add, got and remove.")),
            ("everything", ToolSchema.Boolean("clear: the whole list, not only what's been got."))),
        ToolRisk.WriteOwn);

    public ToolRisk RiskOf(JsonObject arguments) => ToolArgs.String(arguments, "action") == "show" ? ToolRisk.Read : ToolRisk.WriteOwn;

    public string Describe(JsonObject arguments) => $"Shopping list: {ToolArgs.String(arguments, "action")} {string.Join(", ", ToolArgs.Strings(arguments, "items"))}".Trim();

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var items = ToolArgs.Strings(arguments, "items");
        switch (ToolArgs.String(arguments, "action")?.Trim().ToLowerInvariant())
        {
            case "add":
                if (items.Count == 0)
                {
                    return ToolResult.Failure("Say what to put on the list.");
                }

                var (added, already) = await list.AddAsync(context.AccountId, items, ct);
                return ToolResult.Success(
                    new JsonObject { ["ok"] = true, ["added"] = Json(added), ["already_on_it"] = Json(already) },
                    added.Count > 0 ? $"Added {string.Join(", ", added)}" : "Already on the list");

            case "got":
            case "remove":
                if (items.Count == 0)
                {
                    return ToolResult.Failure("Say which things.");
                }

                var got = ToolArgs.String(arguments, "action")!.Trim().Equals("got", StringComparison.OrdinalIgnoreCase);
                var (done, notFound) = got
                    ? await list.TickAsync(context.AccountId, items, true, ct)
                    : await list.RemoveAsync(context.AccountId, items, ct);
                var result = new JsonObject { ["ok"] = done.Count > 0, [got ? "ticked_off" : "removed"] = Json(done) };
                if (notFound.Count > 0)
                {
                    result["not_on_the_list"] = Json(notFound);
                    result["on_the_list"] = Json([.. (await list.ListAsync(context.AccountId, ct)).Where(i => i.GotAt is null).Select(i => i.Text)]);
                }

                return ToolResult.Success(result, done.Count > 0 ? $"{(got ? "Got" : "Removed")} {string.Join(", ", done)}" : "Not on the list");

            case "clear":
                var everything = ToolArgs.Bool(arguments, "everything") == true;
                var cleared = await list.ClearAsync(context.AccountId, everything, ct);
                return ToolResult.Success(new JsonObject { ["ok"] = true, ["cleared"] = cleared, ["everything"] = everything },
                    everything ? "Cleared the shopping list" : "Cleared what's been got");

            case "show":
                var all = await list.ListAsync(context.AccountId, ct);
                var toGet = all.Where(i => i.GotAt is null).Select(i => i.Text).ToList();
                return ToolResult.Success(
                    new JsonObject { ["ok"] = true, ["to_get"] = Json(toGet), ["already_got"] = Json([.. all.Where(i => i.GotAt is not null).Select(i => i.Text)]) },
                    toGet.Count == 0 ? "Shopping list: empty" : $"Shopping list: {toGet.Count} to get");

            default:
                return ToolResult.Failure("Say what to do with the list: add, show, got, remove or clear.");
        }
    }

    private static JsonArray Json(IEnumerable<string> texts) => new([.. texts.Select(t => (JsonNode)JsonValue.Create(t))]);
}
