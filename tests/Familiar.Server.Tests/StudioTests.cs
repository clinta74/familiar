using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Familiar.Server.Familiars;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class StudioTests(PostgresFixture postgres)
{
    /// <summary>The smallest thing that passes for a VRM: a GLB header and a JSON chunk with the extension.</summary>
    internal static byte[] Glb(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var padded = (jsonBytes.Length + 3) / 4 * 4;
        var file = new byte[20 + padded];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)padded);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 0x4E4F534A);
        jsonBytes.CopyTo(file, 20);
        file.AsSpan(20 + jsonBytes.Length).Fill((byte)' ');
        return file;
    }

    internal const string Vrm1 = """
        {"asset":{"version":"2.0"},"extensionsUsed":["VRMC_vrm"],
         "extensions":{"VRMC_vrm":{"specVersion":"1.0","meta":{"name":"Fox","authors":["Kit"],"licenseUrl":"https://vrm.dev/licenses/1.0/","commercialUsage":"personalNonProfit"}}}}
        """;

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] file, string name = "fox.vrm") =>
        client.PostAsync($"/api/avatars?fileName={name}", new ByteArrayContent(file));

    [Fact]
    public async Task The_llama_can_be_coloured_and_dressed_and_bad_values_are_refused()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        (await client.RegisterAsync("stylist")).EnsureSuccessStatusCode();

        var familiar = await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();
        Assert.Equal(AppearanceView.Default, familiar.Appearance);

        var cocoa = new AppearanceView("llama", null, new LlamaColours("#7b5a44", "#e9d3bf", "#5b4030", "#f2a0a0", "#2e8b57"), "scarf", "3d");
        var saved = await (await client.PutAsJsonAsync("/api/familiar", familiar with { Appearance = cocoa })).ReadAsync<FamiliarView>();
        Assert.Equal(cocoa, saved.Appearance);

        // Leaving appearance out keeps it.
        var renamed = await (await client.PutAsJsonAsync("/api/familiar", saved with { Name = "Cocoa", Appearance = null })).ReadAsync<FamiliarView>();
        Assert.Equal(cocoa, renamed.Appearance);

        foreach (var bad in new[]
        {
            cocoa with { Accessory = "crown" },
            cocoa with { Colours = cocoa.Colours with { Wool = "red; background:url(x)" } },
            cocoa with { Model = "vrm", VrmId = Guid.NewGuid() },
            cocoa with { Model = "dragon" },
            cocoa with { Style = "4d" },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/familiar", renamed with { Appearance = bad })).StatusCode);
        }
    }

    [Fact]
    public async Task An_uploaded_vrm_can_be_chosen_fetched_only_by_its_owner_and_deleted()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        (await client.RegisterAsync("modeller")).EnsureSuccessStatusCode();

        var file = Glb(Vrm1);
        var uploaded = await (await UploadAsync(client, file)).ReadAsync<AvatarView>();
        Assert.Equal(("Fox", "1.0", "Kit", "personalNonProfit"), (uploaded.Name, uploaded.Meta.Version, uploaded.Meta.Authors.Single(), uploaded.Meta.CommercialUse));

        var model = await client.GetAsync($"/api/avatars/{uploaded.Id}/model");
        Assert.Equal("model/gltf-binary", model.Content.Headers.ContentType?.MediaType);
        Assert.Equal(file, await model.Content.ReadAsByteArrayAsync());
        // (Cache-Control varies: the cookie middleware says no-store whenever it re-issues the cookie.)
        Assert.NotNull(model.Headers.ETag);

        var familiar = await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();
        var chosen = familiar.Appearance! with { Model = "vrm", VrmId = uploaded.Id };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/familiar", familiar with { Appearance = chosen })).StatusCode);

        var other = app.CreateClient();
        (await other.RegisterAsync("borrower")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/avatars/{uploaded.Id}/model")).StatusCode);
        var theirs = await (await other.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PutAsJsonAsync("/api/familiar", theirs with { Appearance = chosen })).StatusCode);

        // Deleting the model in use brings the llama back.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/avatars/{uploaded.Id}")).StatusCode);
        var after = await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();
        Assert.Equal(("llama", (Guid?)null), (after.Appearance!.Model, after.Appearance.VrmId));
        Assert.Empty(await (await client.GetAsync("/api/avatars")).ReadAsync<List<AvatarView>>());
    }

    [Fact]
    public async Task Things_that_arent_vrm_models_are_refused()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        (await client.RegisterAsync("prankster")).EnsureSuccessStatusCode();

        var plainGltf = Glb("""{"asset":{"version":"2.0"}}""");
        var truncated = Glb(Vrm1)[..40];
        foreach (var bad in new[] { Encoding.UTF8.GetBytes("<html>not a model</html>"), plainGltf, truncated })
        {
            var response = await UploadAsync(client, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Contains("not a VRM", await (await UploadAsync(client, plainGltf)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_built_in_character_can_be_chosen_and_the_familiar_is_told_what_it_looks_like()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        (await client.RegisterAsync("shapeshifter")).EnsureSuccessStatusCode();
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(Conversation.EventTypes.Ready);

        foreach (var (model, look) in new[] { ("fox", "plush fox"), ("hedgehog", "plush hedgehog"), ("monster", "fuzzy, friendly monster"), ("otter", "plush otter"), ("giraffe", "plush giraffe"), ("spot", "spot of colour with no face"), ("llama", "plush llama") })
        {
            var familiar = await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();
            var saved = await (await client.PutAsJsonAsync("/api/familiar", familiar with { Appearance = familiar.Appearance! with { Model = model } })).ReadAsync<FamiliarView>();
            Assert.Equal(model, saved.Appearance!.Model);

            await client.PostAsJsonAsync("/api/conversation/messages", new Conversation.SendMessageRequest("what are you?", "UTC"));
            await stream.ReadUntilStateAsync(Conversation.FamiliarState.Idle);
            var persona = app.Llm.LastPrompt![0].Content;
            Assert.Contains(look, persona, StringComparison.Ordinal);
            Assert.Single(new[] { "plush llama", "plush fox", "plush hedgehog", "monster", "plush otter", "plush giraffe", "spot of colour" }, l => persona.Contains(l, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Vrm_0_metadata_is_read_too()
    {
        var (info, problem) = VrmFile.Inspect(Glb("""
            {"asset":{"version":"2.0"},"extensions":{"VRM":{"meta":{"title":"Old","author":"Someone","licenseName":"CC_BY","commercialUssageName":"Allow"}}}}
            """));

        Assert.Null(problem);
        Assert.Equal(new VrmInfo("0.x", "Old", ["Someone"], "CC_BY", "Allow").ToString(), info!.ToString());
    }
}
