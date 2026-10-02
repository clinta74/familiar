using System.Net;
using System.Text;
using Familiar.Agent.Llm;

namespace Familiar.Server.Tests;

public sealed class OllamaWarmUpTests
{
    /// <summary>Just enough Ollama: /api/ps lists what's loaded; generate and embed load a model if it exists.</summary>
    private sealed class FakeOllama(params string[] existing) : HttpMessageHandler
    {
        public HashSet<string> Loaded { get; } = [];

        public List<string> Calls { get; } = [];

        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("Connection refused");
            }

            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(path);
            if (path == "/api/ps")
            {
                var models = string.Join(",", Loaded.Select(m => $$"""{"name":"{{m}}:latest"}"""));
                return Json($$"""{"models":[{{models}}]}""");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var model = existing.FirstOrDefault(m => body.Contains($"\"{m}\"", StringComparison.Ordinal));
            if (model is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"model not found"}""") };
            }

            Loaded.Add(model);
            return Json("""{"done":true}""");
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static OllamaWarmUp WarmUp(FakeOllama ollama) =>
        new(new HttpClient(ollama) { BaseAddress = new Uri("http://ollama.test/") }, new LlmOptions());

    [Fact]
    public async Task Loads_both_models_once_and_then_just_checks()
    {
        var ollama = new FakeOllama("familiar-chat", "nomic-embed-text");
        var warmUp = WarmUp(ollama);

        Assert.Equal(WarmUpOutcome.Loaded, await warmUp.EnsureLoadedAsync(default));
        Assert.Equal(["familiar-chat", "nomic-embed-text"], ollama.Loaded.Order());
        Assert.Equal(["/api/ps", "/api/generate", "/api/embed"], ollama.Calls);

        ollama.Calls.Clear();
        Assert.Equal(WarmUpOutcome.AlreadyLoaded, await warmUp.EnsureLoadedAsync(default));
        Assert.Equal(["/api/ps"], ollama.Calls);

        // Ollama restarted: everything's gone, so it's loaded again.
        ollama.Loaded.Clear();
        Assert.Equal(WarmUpOutcome.Loaded, await warmUp.EnsureLoadedAsync(default));
    }

    [Fact]
    public async Task Says_which_model_is_missing()
    {
        var warmUp = WarmUp(new FakeOllama("nomic-embed-text"));

        Assert.Equal(WarmUpOutcome.Missing, await warmUp.EnsureLoadedAsync(default));
        Assert.Equal("familiar-chat", warmUp.MissingModel);
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_not_thrown() =>
        Assert.Equal(WarmUpOutcome.Unreachable, await WarmUp(new FakeOllama { Down = true }).EnsureLoadedAsync(default));
}
