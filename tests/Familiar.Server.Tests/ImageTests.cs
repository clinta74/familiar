using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Familiar.Agent.Llm;
using Familiar.Server.Conversation;
using Familiar.Server.Tests.Infrastructure;
using Familiar.Server.Tools;

namespace Familiar.Server.Tests;

/// <summary>BACKLOG item 35: pictures in the chat - kept without their metadata, seen with their own turn only.</summary>
public static class TestPictures
{
    /// <summary>A real 4×3 JPEG (Pillow, quality 80).</summary>
    public static readonly byte[] Plain = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAADAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCzRRRXwR9sf//Z");

    /// <summary>The same picture as a camera writes it: EXIF with a GPS position, and a comment.</summary>
    public static byte[] FromACamera()
    {
        static byte[] Segment(byte marker, string text)
        {
            var body = Encoding.ASCII.GetBytes(text);
            var length = body.Length + 2;
            return [0xFF, marker, (byte)(length >> 8), (byte)length, .. body];
        }

        return [.. Plain[..2], .. Segment(0xE1, "Exif\0\0GPS 42.4904,-83.0130 iPhone"), .. Segment(0xFE, "taken at home"), .. Plain[2..]];
    }
}

public sealed class ImageUnitTests
{
    [Fact]
    public void A_camera_picture_loses_its_location_and_keeps_its_picture()
    {
        var (bytes, width, height) = ChatImages.Clean(TestPictures.FromACamera());

        Assert.Equal((4, 3), (width, height));
        Assert.Equal(TestPictures.Plain, bytes);
        Assert.DoesNotContain("GPS", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_whole_jpeg_is_kept()
    {
        Assert.Throws<ChatImageException>(() => ChatImages.Clean(Encoding.ASCII.GetBytes("GIF89a not a jpeg")));
        Assert.Throws<ChatImageException>(() => ChatImages.Clean(TestPictures.Plain[..300]));
        Assert.Throws<ChatImageException>(() => ChatImages.Clean([0xFF, 0xD8, 0xFF, 0xD9]));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ImageTests(PostgresFixture postgres)
{
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] jpeg)
    {
        var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return await client.PostAsync("/api/conversation/images", content);
    }

    [Fact]
    public async Task A_picture_is_seen_with_its_own_message_and_only_said_to_have_been_there_after()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenSay("[joy:0.6] That's a lovely orange.").ThenSay("[relaxed:0.4] It was orange.");
        var client = app.CreateClient();
        (await client.RegisterAsync("snapper")).EnsureSuccessStatusCode();

        var image = await (await UploadAsync(client, TestPictures.FromACamera())).ReadAsync<ImageView>();
        Assert.Equal((4, 3), (image.Width, image.Height));

        // What's kept is the picture without the camera's notes.
        var kept = await client.GetByteArrayAsync($"/api/conversation/images/{image.Id}");
        Assert.Equal(TestPictures.Plain, kept);

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        var sent = await (await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("", "UTC", ImageId: image.Id))).ReadAsync<MessageView>();
        Assert.Equal(image.Id, sent.ImageId);
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        var withPicture = app.Llm.LastPrompt![^1];
        Assert.Equal(TestPictures.Plain, Assert.Single(withPicture.Images!));
        Assert.Contains("They sent this picture.", withPicture.Content, StringComparison.Ordinal);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("what colour was it?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.All(app.Llm.LastPrompt!, m => Assert.Null(m.Images));
        Assert.Contains(app.Llm.LastPrompt!, m => m.Role == ChatRole.User && m.Content.StartsWith("(They sent a picture here", StringComparison.Ordinal));

        // Once sent it can't be sent again, and it's nobody else's to see.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("again", "UTC", ImageId: image.Id))).StatusCode);
        var stranger = app.CreateClient();
        (await stranger.RegisterAsync("stranger")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/conversation/images/{image.Id}")).StatusCode);
        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.Equal(image.Id, history.Single(m => m.Id == sent.Id).ImageId);
    }

    [Fact]
    public async Task Words_in_a_picture_cannot_quietly_change_things()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("set_timer", """{"minutes": 5, "label": "note"}""").ThenSay("[relaxed:0.4] Okay.");
        var client = app.CreateClient();
        (await client.RegisterAsync("careful")).EnsureSuccessStatusCode();
        var image = await (await UploadAsync(client, TestPictures.Plain)).ReadAsync<ImageView>();

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("what does this note say?", "UTC", ImageId: image.Id));

        // Setting a timer usually just happens; after a picture, it asks.
        var confirm = (await stream.ReadUntilAsync(EventTypes.ToolConfirm))[^1].Payload<ToolConfirmEvent>();
        Assert.Equal("set_timer", confirm.Tool);
    }

    [Fact]
    public async Task Only_a_jpeg_of_a_sensible_size_is_taken()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        (await client.RegisterAsync("uploader")).EnsureSuccessStatusCode();

        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/conversation/images", png)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, TestPictures.Plain[..200])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("", "UTC", ImageId: Guid.NewGuid()))).StatusCode);
    }
}
