using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Agent.Tools;
using Familiar.Domain.Shopping;
using Familiar.Server.Conversation;
using Familiar.Server.Devices;
using Familiar.Server.Shopping;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

/// <summary>The shopping list by voice and on screen, and the hand-off to a store's app.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ShoppingTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app, string username = "shopper")
    {
        var client = app.CreateClient();
        (await client.RegisterAsync(username)).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> SayAsync(HttpClient client, string text) =>
        client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest(text, "UTC"));

    private static async Task<List<ShoppingItemView>> ListAsync(HttpClient client) =>
        await (await client.GetAsync("/api/shopping")).ReadAsync<List<ShoppingItemView>>();

    [Fact]
    public async Task The_list_by_voice_adds_ticks_off_and_clears()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm
            .ThenCall("shopping_list", """{"action": "add", "items": ["milk", "AA batteries", "mac and cheese"]}""").ThenSay("[joy:0.5] Added.")
            .ThenCall("shopping_list", """{"action": "add", "items": "Milk, bread"}""").ThenSay("[joy:0.5] Bread too.")
            .ThenCall("shopping_list", """{"action": "got", "items": ["the milk", "batteries"]}""").ThenSay("[joy:0.5] Ticked off.")
            .ThenCall("shopping_list", """{"action": "show"}""").ThenSay("[neutral:0.5] Bread and mac and cheese.")
            .ThenCall("shopping_list", """{"action": "clear"}""").ThenSay("[neutral:0.5] Cleared.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "add milk, AA batteries and mac and cheese to the shopping list");
        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        // Every open tab and app hears the list change.
        var heard = events.Last(e => e.Type == EventTypes.Shopping).Payload<ShoppingEvent>();
        Assert.Equal(["milk", "AA batteries", "mac and cheese"], heard.Items.Select(i => i.Text));

        // "Milk" is on it already; a list in one string is split at the commas.
        await SayAsync(client, "and milk and bread");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("\"already_on_it\":[\"milk\"]", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);

        await SayAsync(client, "I got the milk and the batteries");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var list = await ListAsync(client);
        Assert.Equal(["mac and cheese", "bread"], list.Where(i => i.GotAt is null).Select(i => i.Text));
        Assert.Equal(["milk", "AA batteries"], list.Where(i => i.GotAt is not null).Select(i => i.Text));

        await SayAsync(client, "what's on the list?");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("\"to_get\":[\"mac and cheese\",\"bread\"]", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);

        // Clearing takes only what's been got, unless they say the whole list.
        await SayAsync(client, "clear off what I got");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Equal(["mac and cheese", "bread"], (await ListAsync(client)).Select(i => i.Text));
    }

    [Fact]
    public async Task On_screen_items_are_added_ticked_and_deleted()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        var added = await (await client.PostAsJsonAsync("/api/shopping", new ShoppingItemRequest("eggs"))).ReadAsync<List<ShoppingItemView>>();
        var eggs = Assert.Single(added);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/shopping", new ShoppingItemRequest(" "))).StatusCode);

        var ticked = await (await client.PutAsJsonAsync($"/api/shopping/{eggs.Id}", new ShoppingTickRequest(true))).ReadAsync<ShoppingItemView>();
        Assert.NotNull(ticked.GotAt);

        // Adding it again puts it back to get.
        await client.PostAsJsonAsync("/api/shopping", new ShoppingItemRequest("Eggs"));
        Assert.Null(Assert.Single(await ListAsync(client)).GotAt);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/shopping/{eggs.Id}")).StatusCode);
        Assert.Empty(await ListAsync(client));

        // Someone else's list is theirs.
        await client.PostAsJsonAsync("/api/shopping", new ShoppingItemRequest("tea"));
        var other = await SignedInAsync(app, "neighbour");
        Assert.Empty(await ListAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/shopping/{(await ListAsync(client))[0].Id}")).StatusCode);
    }

    [Theory]
    [InlineData("milk", "Milk")]
    [InlineData("the milk", "Milk")]
    [InlineData("batteries", "AA Batteries")]
    [InlineData("tomato", "tomatoes")]
    [InlineData("bread", null)]
    public void What_is_said_matches_what_is_on_the_list(string said, string? expected)
    {
        ShoppingItem[] items = [new() { Text = "Milk" }, new() { Text = "AA Batteries" }, new() { Text = "tomatoes" }];
        Assert.Equal(expected, ShoppingList.Matching(items, said).SingleOrDefault()?.Text);
    }

    [Fact]
    public void A_list_comes_however_the_model_sends_it()
    {
        Assert.Equal(["milk", "eggs"], ToolArgs.Strings(new JsonObject { ["items"] = new JsonArray("milk", " eggs ") }, "items"));
        Assert.Equal(["milk", "salt and pepper"], ToolArgs.Strings(new JsonObject { ["items"] = "milk, salt and pepper." }, "items"));
        Assert.Empty(ToolArgs.Strings(new JsonObject(), "items"));
    }

    [Theory]
    [InlineData("amazon", "US", "https://www.amazon.com/s?k=AA%20batteries")]
    [InlineData("amazon", "GB", "https://www.amazon.co.uk/s?k=AA%20batteries")]
    [InlineData("target", null, "https://www.target.com/s?searchTerm=AA%20batteries")]
    [InlineData("walmart", null, "https://www.walmart.com/search?q=AA%20batteries")]
    [InlineData("kroger", null, "https://www.kroger.com/search?query=AA%20batteries")]
    [InlineData("meijer", null, "https://www.meijer.com/shopping/search.html?text=AA%20batteries")]
    [InlineData("costco", null, "https://www.costco.com/s?keyword=AA%20batteries")]
    public void Each_store_opens_at_its_own_search(string store, string? country, string expected) =>
        Assert.Equal(expected, Stores.SearchUrl(store, "AA batteries", country));

    [Fact]
    public async Task Shop_hands_off_to_the_phone_with_the_stores_search()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("shop", """{"store": "kroger", "search": "oat milk"}""").ThenSay("[joy:0.5] It's open on your phone.");
        var browser = await SignedInAsync(app, "pat");
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("pat")).Token);

        // Offered only while a phone that can is connected.
        await using var tab = await SseClient.ConnectAsync(browser);
        await tab.ReadUntilAsync(EventTypes.Ready);
        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "shop.open");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(browser, "find oat milk at Kroger");
        var invoke = (await phoneStream.ReadUntilAsync(EventTypes.ToolInvoke))[^1].Payload<ToolInvokeEvent>();
        Assert.Equal("shop.open", invoke.Tool);
        Assert.Equal(("https://www.kroger.com/search?query=oat%20milk", "Kroger: oat milk"),
            (invoke.Arguments["url"]!.GetValue<string>(), invoke.Arguments["title"]!.GetValue<string>()));
        await phone.PostAsJsonAsync("/api/conversation/tool-result", new ToolResultRequest(invoke.CallId, true, new JsonObject { ["how"] = "Opened" }));
        await tab.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("shop", app.Llm.LastTools!.Select(t => t.Name));
    }
}
