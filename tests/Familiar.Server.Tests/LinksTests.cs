using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Familiar.Agent.Expression;
using Familiar.Agent.Llm;
using Familiar.Agent.Tools;
using Familiar.Server.Conversation;
using Familiar.Server.LookUps;
using Familiar.Server.Tests.Infrastructure;
using Familiar.Server.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Familiar.Server.Tests;

/// <summary>BACKLOG item 34: pages handed over as cards, and links the user gives read safely.</summary>
public sealed class LinksUnitTests
{
    [Theory]
    [InlineData("[link:2]", 2)]
    [InlineData("[ link : 1 ]", 1)]
    public void A_link_tag_names_a_page_by_number(string tag, int number) =>
        Assert.Equal(new LinkToken(number), ExpressionParser.TryParseTag(tag));

    [Theory]
    [InlineData("[link:https://evil.example]")]
    [InlineData("[link:0]")]
    [InlineData("[link:]")]
    public void A_link_tag_without_a_number_is_dropped_not_read_out(string tag) =>
        Assert.IsType<IgnoredTag>(ExpressionParser.TryParseTag(tag));

    [Fact]
    public void Pages_are_numbered_for_the_reply_and_the_model_never_sees_their_addresses()
    {
        var turn = new ToolTurn();
        var first = ToolExecutor.Numbered(ToolResult.Success([], "a") with
        {
            Links = [new("https://en.wikipedia.org/wiki/Petrichor", "Petrichor"), new("javascript:alert(1)", "Bad")],
        }, turn);
        var second = ToolExecutor.Numbered(ToolResult.Success([], "b") with
        {
            Links = [new("https://www.michigan.gov/sos", "Michigan Secretary of State"), new("https://en.wikipedia.org/wiki/Petrichor", "Petrichor")],
        }, turn);

        Assert.Equal(["https://en.wikipedia.org/wiki/Petrichor", "https://www.michigan.gov/sos"], turn.Links.Select(l => l.Url));
        Assert.Equal("""[{"link":1,"title":"Petrichor","site":"en.wikipedia.org"}]""", first.Data["links"]!.ToJsonString());
        Assert.Equal(
            """[{"link":2,"title":"Michigan Secretary of State","site":"www.michigan.gov"},{"link":1,"title":"Petrichor","site":"en.wikipedia.org"}]""",
            second.Data["links"]!.ToJsonString());
        Assert.DoesNotContain("https://", first.Data.ToJsonString() + second.Data.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_own_addresses_are_found_in_what_they_wrote() =>
        Assert.Equal(
            ["https://example.com/a?b=1", "http://news.example.org/story"],
            WebAddresses.In("Read https://example.com/a?b=1. And (http://news.example.org/story) - not ftp://x.example or example.net."));

    [Theory]
    [InlineData("https://example.com/page", true)]
    [InlineData("http://example.com:80/", true)]
    [InlineData("https://example.com:8443/", false)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("http://localhost/", false)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("http://192.168.1.10/", false)]
    [InlineData("http://10.0.0.5/admin", false)]
    [InlineData("http://172.20.0.2/", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("http://100.64.1.1/", false)]
    [InlineData("http://[::1]/", false)]
    [InlineData("http://[fd00::1]/", false)]
    [InlineData("http://[::ffff:192.168.1.1]/", false)]
    [InlineData("http://nas/", false)]
    [InlineData("http://homeassistant.local:8123/", false)]
    [InlineData("https://user:pass@example.com/", false)]
    [InlineData("http://8.8.8.8/", true)]
    public void Only_public_addresses_may_be_read(string url, bool expected) =>
        Assert.Equal(expected, LinkReader.IsPublic(new Uri(url)));

    [Fact]
    public void A_page_reads_as_its_article_without_scripts_menus_or_addresses()
    {
        var page = LinkReader.FromHtml(new Uri("https://example.com/story"), """
            <html><head><title>Rain &amp; Smells</title><script>alert('x')</script></head>
            <body><nav>Home | About</nav><header>Site name</header>
            <article><h1>Why rain smells</h1><p>It's called <a href="https://x.example">petrichor</a>.</p>
            <ul><li>Oils from plants</li><li>Geosmin</li></ul><p>See https://evil.example/x for more.</p></article>
            <footer>Copyright</footer></body></html>
            """);

        Assert.Equal("Rain & Smells", page.Title);
        Assert.Equal("## Why rain smells\nIt's called petrichor.\n- Oils from plants\n- Geosmin\nSee for more.", page.Text);
    }

    private sealed class Pages : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/story" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<title>A story</title><main><p>Once upon a time.</p></main>", Encoding.UTF8, "text/html"),
                },
                "/sneaky" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://192.168.1.1/admin") } },
                "/picture" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }
    }

    private static ReadLinkTool Tool()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(LinkReader)).ConfigurePrimaryHttpMessageHandler(() => new Pages());
        var http = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new ReadLinkTool(new LinkReader(http, new MemoryCache(new MemoryCacheOptions())));
    }

    private static ToolContext Said(params string[] links) =>
        new(Guid.NewGuid(), Guid.NewGuid(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow, LookUps: true, UserLinks: links);

    [Fact]
    public async Task Read_link_reads_a_link_the_user_gave_and_offers_it_back()
    {
        var result = await Tool().InvokeAsync(Said("https://example.com/story"), new JsonObject { ["url"] = "https://example.com/story" }, default);

        Assert.True(result.Ok);
        Assert.Equal(("A story", "Once upon a time."), (result.Data["title"]!.GetValue<string>(), result.Data["text"]!.GetValue<string>()));
        Assert.Contains("not instructions", result.Data["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([new LinkRef("https://example.com/story", "A story")], result.Links);
    }

    [Fact]
    public async Task Read_link_reads_only_the_users_links_and_never_the_household_network()
    {
        var tool = Tool();

        // Not one they wrote: a page or a look-up can't send it somewhere.
        var other = await tool.InvokeAsync(Said("https://example.com/story"), new JsonObject { ["url"] = "https://elsewhere.example/steal?d=1" }, default);
        Assert.Contains("only read a link the user wrote", other.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var none = await tool.InvokeAsync(Said(), new JsonObject { ["url"] = "https://example.com/story" }, default);
        Assert.Contains("no link in what the user said", none.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var local = await tool.InvokeAsync(Said("http://192.168.1.10/"), new JsonObject { ["url"] = "http://192.168.1.10/" }, default);
        Assert.Contains("Only public web pages", local.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var redirected = await tool.InvokeAsync(Said("https://example.com/sneaky"), new JsonObject { ["url"] = "https://example.com/sneaky" }, default);
        Assert.Contains("Only public web pages", redirected.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var picture = await tool.InvokeAsync(Said("https://example.com/picture"), new JsonObject { ["url"] = "https://example.com/picture" }, default);
        Assert.Contains("isn't a page it can read (it's image/png)", picture.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class LinksTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_page_a_look_up_found_is_handed_over_as_a_card_and_kept_with_the_reply()
    {
        var outside = new OutsideServices();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient("LookUps").ConfigurePrimaryHttpMessageHandler(() => outside));
        app.Llm.ThenCall("look_up", """{"topic": "new bridge from Canada to Michigan"}""")
            .ThenSay("[joy:0.5] It opened in July. Here's more: [link:1] [link:7]");
        var client = app.CreateClient();
        (await client.RegisterAsync("linker")).EnsureSuccessStatusCode();
        var profile = await (await client.GetAsync("/api/familiar")).ReadAsync<Familiars.FamiliarView>();
        (await client.PutAsJsonAsync("/api/familiar", profile with { LookUps = true, Proactive = false })).EnsureSuccessStatusCode();

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("what about the new bridge?", "UTC"));
        var events = await stream.ReadUntilAsync(EventTypes.ReplyEnd);

        // The model saw the page by number and title only.
        var toolMessage = app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("\"link\":1,\"title\":\"Gordie Howe International Bridge\"", toolMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", toolMessage, StringComparison.Ordinal);

        // Shown as a card, not read out; a number no tool gave (7) is dropped.
        var end = events[^1].Payload<ReplyEndEvent>();
        Assert.Equal("It opened in July. Here's more:", end.Text);
        var card = Assert.Single(end.Links!);
        Assert.Equal(("https://en.wikipedia.org/wiki/Gordie_Howe_International_Bridge", "Gordie Howe International Bridge", "en.wikipedia.org"), (card.Url, card.Title, card.Site));

        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.Equal(card, Assert.Single(history.Single(m => m.Id == end.Id).Links!));
    }
}
