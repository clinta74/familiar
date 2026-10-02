// Phase 0 model bake-off (PLAN.md §4.1): which base model should Familiar run?
//
//   dotnet run tools/ollama/bakeoff.cs -- --models qwen3:8b,llama3.1:8b,mistral-nemo:12b
//   dotnet run tools/ollama/bakeoff.cs -- --url http://localhost:11435 --models qwen3:8b --runs 3
//
// Pull the candidates first (`docker exec familiar-ollama ollama pull <model>`). Each model is
// scored on what Familiar needs from it:
//
//   tools    - picks the right tool, with sane arguments, when one is called for
//   restraint - calls no tool when none is called for (chit-chat must not set timers)
//   emotion  - opens a spoken reply with a valid [emotion:intensity] tag (PLAN.md §4.4)
//   speed    - prompt eval and generation tok/s, from Ollama's own timings
//
// num_ctx is sent per request here so every candidate runs with the window Familiar will use,
// whether or not a derived model has been built over it yet.

// A throwaway measuring tool, not something to trim or AOT-compile (file-based apps default to AOT).
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var url = Arg("--url") ?? "http://localhost:11435";
var models = (Arg("--models") ?? "qwen3:8b").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var runs = int.Parse(Arg("--runs") ?? "2", CultureInfo.InvariantCulture);
var numCtx = int.Parse(Arg("--ctx") ?? "8192", CultureInfo.InvariantCulture);

using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(10) };

const string System = """
    You are Familiar, a warm, upbeat companion who speaks aloud through an animated avatar.
    Replies are spoken, so keep them to one to three short sentences, with no markdown or lists.
    When the user asks for something one of your tools does, call the tool; don't just say you will.
    Otherwise answer in speech, and begin the spoken reply with exactly one emotion tag of the form
    [emotion:intensity], where emotion is one of joy, sad, angry, surprised, relaxed, neutral,
    thinking and intensity is between 0 and 1. Example: [joy:0.6] That sounds wonderful!
    The current time is 2026-10-01 17:30.
    """;

// Found in the first run: "Begin every reply with an emotion tag" made qwen3:8b answer every tool
// request in speech ("Got it, I'll set a timer!") and never call the tool - the worst failure an
// assistant can have, because it claims an action it didn't take. The tag instruction has to be
// scoped to spoken replies, and Familiar's real system prompt (phase 1) must keep that wording.

var tools = JsonNode.Parse("""
    [
      { "type": "function", "function": { "name": "set_timer", "description": "Start a countdown timer.",
        "parameters": { "type": "object", "required": ["duration_seconds"], "properties": {
          "duration_seconds": { "type": "integer", "description": "Length of the timer in seconds" },
          "label": { "type": "string", "description": "What the timer is for" } } } } },
      { "type": "function", "function": { "name": "cancel_timer", "description": "Cancel a running timer by its label.",
        "parameters": { "type": "object", "required": ["label"], "properties": {
          "label": { "type": "string" } } } } },
      { "type": "function", "function": { "name": "list_timers", "description": "List the timers that are running.",
        "parameters": { "type": "object", "properties": {} } } },
      { "type": "function", "function": { "name": "create_reminder", "description": "Remind the user about something at a date and time.",
        "parameters": { "type": "object", "required": ["text", "due"], "properties": {
          "text": { "type": "string" },
          "due": { "type": "string", "description": "ISO 8601 local date-time" } } } } },
      { "type": "function", "function": { "name": "get_calendar", "description": "Read the user's calendar events for a day.",
        "parameters": { "type": "object", "required": ["date"], "properties": {
          "date": { "type": "string", "description": "ISO 8601 date" } } } } }
    ]
    """)!;

var toolNames = tools.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToArray();

// (prompt, expected tool or null, argument check)
Case[] cases =
[
    new("Set a timer for ten minutes for the pasta.", "set_timer", a => Int(a, "duration_seconds") == 600),
    new("Can you start a 90 second timer?", "set_timer", a => Int(a, "duration_seconds") == 90),
    new("Cancel the pasta timer.", "cancel_timer", a => Str(a, "label")?.Contains("pasta", StringComparison.OrdinalIgnoreCase) == true),
    new("What timers have I got going?", "list_timers", _ => true),
    new("Remind me to call my mum tomorrow at 9am.", "create_reminder", a => Str(a, "due")?.StartsWith("2026-10-02T09", StringComparison.Ordinal) == true),
    new("What's on my calendar tomorrow?", "get_calendar", a => Str(a, "date")?.StartsWith("2026-10-02", StringComparison.Ordinal) == true),
    new("Hi! How are you doing today?", null, _ => true),
    new("I finally finished that painting I was working on!", null, _ => true),
    new("My dog has been sick all week and I'm worried.", null, _ => true),
    new("Tell me something interesting about octopuses.", null, _ => true),
    new("I've been thinking about timers in old cameras, how did they work?", null, _ => true),
];

var emotionTag = new Regex(@"^\s*\[(joy|sad|angry|surprised|relaxed|neutral|thinking):(0(\.\d+)?|1(\.0+)?)\]", RegexOptions.Compiled);
var results = new List<string>();

foreach (var model in models)
{
    var show = await http.PostAsJsonAsync("/api/show", new { model });
    if (!show.IsSuccessStatusCode)
    {
        Console.WriteLine($"{model}: not available ({(int)show.StatusCode}); pull it first.");
        continue;
    }

    var capabilities = (await show.Content.ReadFromJsonAsync<JsonObject>())?["capabilities"]?.AsArray()
        .Select(c => c!.GetValue<string>()).ToHashSet() ?? [];
    if (!capabilities.Contains("tools"))
    {
        Console.WriteLine($"{model}: no 'tools' capability - can't drive Familiar's tools, skipped.");
        continue;
    }

    var thinks = capabilities.Contains("thinking");
    int toolRight = 0, toolTotal = 0, quietRight = 0, quietTotal = 0, tagged = 0, replies = 0;
    double promptTokens = 0, promptSeconds = 0, genTokens = 0, genSeconds = 0;
    var firstTokenMs = new List<double>();

    Console.WriteLine($"\n== {model}{(thinks ? " (thinking disabled)" : "")}");

    // Warm-up: load the model so the first case doesn't pay for it.
    await Chat(model, "Hello.", thinks);

    for (var run = 0; run < runs; run++)
    {
        foreach (var c in cases)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await Chat(model, c.Prompt, thinks);
            stopwatch.Stop();

            var message = response["message"]!;
            var call = message["tool_calls"]?.AsArray().FirstOrDefault()?["function"];
            var content = message["content"]?.GetValue<string>() ?? string.Empty;

            promptTokens += Num(response, "prompt_eval_count");
            promptSeconds += Num(response, "prompt_eval_duration") / 1e9;
            genTokens += Num(response, "eval_count");
            genSeconds += Num(response, "eval_duration") / 1e9;
            firstTokenMs.Add((Num(response, "load_duration") + Num(response, "prompt_eval_duration")) / 1e6);

            string verdict;
            if (c.Tool is not null)
            {
                toolTotal++;
                var name = call?["name"]?.GetValue<string>();
                var arguments = call?["arguments"] as JsonObject;
                var ok = name == c.Tool && arguments is not null && c.Check(arguments);
                if (ok)
                {
                    toolRight++;
                }

                verdict = ok
                    ? "ok"
                    : name is null
                        ? $"MISS (no call; said: {Truncate(content, 60)})"
                        : $"MISS (got {name} {arguments?.ToJsonString()})";
            }
            else
            {
                quietTotal++;

                // A tool call written into the speech ("I'd like to call get_calendar", or raw
                // {"name": ...} JSON) is a restraint failure too: the avatar would read it aloud.
                var leaked = toolNames.Any(n => content.Contains(n, StringComparison.Ordinal))
                    || content.Contains("\"name\"", StringComparison.Ordinal);
                var ok = call is null && !leaked;
                if (ok)
                {
                    quietRight++;
                }

                replies++;
                var hasTag = emotionTag.IsMatch(content);
                if (hasTag)
                {
                    tagged++;
                }

                verdict = (ok ? "ok" : call is null ? "LEAKED" : $"CALLED {call["name"]}") + (hasTag ? "" : " no-tag");
            }

            Console.WriteLine($"  {stopwatch.ElapsedMilliseconds,6} ms  {verdict,-12} {c.Prompt}");
            if (c.Tool is null)
            {
                Console.WriteLine($"           -> {Truncate(content, 110)}");
            }
        }
    }

    firstTokenMs.Sort();
    var median = firstTokenMs.Count == 0 ? 0 : firstTokenMs[firstTokenMs.Count / 2];
    results.Add(string.Create(CultureInfo.InvariantCulture,
        $"| {model} | {toolRight}/{toolTotal} | {quietRight}/{quietTotal} | {tagged}/{replies} | " +
        $"{promptTokens / Math.Max(promptSeconds, 1e-9):F0} | {genTokens / Math.Max(genSeconds, 1e-9):F1} | {median:F0} |"));
}

Console.WriteLine();
Console.WriteLine($"num_ctx {numCtx}, {runs} run(s) of {cases.Length} cases");
Console.WriteLine("| model | tools | restraint | emotion tag | prompt tok/s | gen tok/s | median ms to first token |");
Console.WriteLine("|---|---|---|---|---|---|---|");
results.ForEach(Console.WriteLine);
return;

async Task<JsonObject> Chat(string model, string prompt, bool thinks)
{
    var body = new JsonObject
    {
        ["model"] = model,
        ["stream"] = false,
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = System },
            new JsonObject { ["role"] = "user", ["content"] = prompt }),
        ["tools"] = tools.DeepClone(),
        ["options"] = new JsonObject { ["num_ctx"] = numCtx, ["temperature"] = 0.3 },
        ["keep_alive"] = -1,
    };

    // Reasoning before every spoken reply costs seconds Familiar doesn't have.
    if (thinks)
    {
        body["think"] = false;
    }

    var response = await http.PostAsJsonAsync("/api/chat", body);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static double Num(JsonObject o, string key) => o[key]?.GetValue<double>() ?? 0;

static int? Int(JsonObject a, string key) => a[key] switch
{
    JsonValue v when v.TryGetValue<int>(out var i) => i,
    JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var i) => i,
    _ => null,
};

static string? Str(JsonObject a, string key) => a[key]?.ToString();

static string Truncate(string s, int max)
{
    s = s.ReplaceLineEndings(" ");
    return s.Length <= max ? s : s[..max] + "…";
}

internal sealed record Case(string Prompt, string? Tool, Func<JsonObject, bool> Check);
