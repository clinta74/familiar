using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;

namespace Familiar.Server.LookUps;

/// <summary>
/// read_link: a web page the user gave the familiar - a link in one of their messages - read as text
/// so it can be summed up or asked about (BACKLOG item 34). Only their links: an address in a look-up
/// or another page can't send it anywhere. A look-up like the others, so it's off unless look-ups are
/// on, counts towards the reply's three, and anything that changes something afterwards asks first.
/// </summary>
public sealed class ReadLinkTool(LinkReader reader) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "read_link",
        "Reads a web page the user gave you - a link in one of their messages - so you can answer about it: what it says, a " +
        "summary, something in it. Whenever their message has a link and they ask about it, call this; it says itself if the " +
        "page can't be read. Only links the user wrote; never a page a look-up or another page mentioned.",
        ToolSchema.Object(["url"], ("url", ToolSchema.String("The link, exactly as the user wrote it."))),
        ToolRisk.LookUp);

    public string Describe(JsonObject arguments) => $"Read {ToolArgs.String(arguments, "url")}";

    public async Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var given = ToolArgs.String(arguments, "url");
        var wrote = context.UserLinks ?? [];
        if (given is null || !Uri.TryCreate(given, UriKind.Absolute, out var url)
            || !wrote.Any(l => Same(l, url.AbsoluteUri)))
        {
            return ToolResult.Failure(wrote.Count == 0
                ? "There's no link in what the user said. Ask them to paste the link."
                : $"You can only read a link the user wrote: {string.Join(", ", wrote)}.");
        }

        try
        {
            var page = await reader.ReadAsync(url, ct);
            return ToolResult.Success(LookUpText.Result(page.Url.Host, new JsonObject
            {
                ["ok"] = true,
                ["title"] = page.Title,
                ["site"] = page.Url.Host,
                ["text"] = page.Text,
            }), $"Read: {page.Title}") with { Links = [new LinkRef(page.Url.AbsoluteUri, page.Title)] };
        }
        catch (LinkException exception)
        {
            return ToolResult.Failure($"Couldn't read it: {exception.Message} Say so.", "Couldn't read the link");
        }
    }

    /// <summary>The same page, give or take a trailing slash.</summary>
    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
}
