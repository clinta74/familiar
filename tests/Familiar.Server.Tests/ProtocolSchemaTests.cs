using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Familiar.Server.Conversation;
using Familiar.Server.Protocol;

namespace Familiar.Server.Tests;

/// <summary>
/// The protocol schema (MOBILE.md §6) is generated from the server's records and committed, and the
/// clients' types are generated from it: these keep it complete and current. To update it after
/// changing a record: <c>FAMILIAR_WRITE_PROTOCOL=1 dotnet test --filter ProtocolSchemaTests</c>, then
/// <c>npm run protocol</c> in client/ for the TypeScript and Kotlin.
/// </summary>
public sealed class ProtocolSchemaTests
{
    /// <summary>
    /// The repository's copy. From this source file's path where it's real; CI builds are deterministic
    /// (paths mapped to /_/), so otherwise the first folder up from the tests with the solution in it.
    /// </summary>
    private static string SchemaPath([CallerFilePath] string here = "")
    {
        var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        var root = File.Exists(Path.Combine(fromSource, "Familiar.slnx"))
            ? fromSource
            : new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
                .SelectMany(start => Ancestors(new DirectoryInfo(start)))
                .FirstOrDefault(dir => File.Exists(Path.Combine(dir.FullName, "Familiar.slnx")))?.FullName
                ?? fromSource;
        return Path.Combine(root, "protocol", "familiar.schema.json");
    }

    private static IEnumerable<DirectoryInfo> Ancestors(DirectoryInfo? dir)
    {
        for (; dir is not null; dir = dir.Parent)
        {
            yield return dir;
        }
    }

    [Fact]
    public void Every_stream_event_is_in_the_protocol()
    {
        var declared = typeof(EventTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(declared, ProtocolCatalog.Events.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_schema_says_what_goes_over_the_wire()
    {
        var schema = JsonNode.Parse(ProtocolSchema.Generate())!;
        var defs = schema["$defs"]!;

        // camelCase names, every property present, nullability as written.
        var audio = defs["AudioEvent"]!;
        Assert.Equal(["replyId", "seq", "url", "text", "words"], audio["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("#/$defs/SpokenWord", audio["properties"]!["words"]!["items"]!["$ref"]!.GetValue<string>());
        Assert.Equal("null", audio["properties"]!["words"]!["type"]![1]!.GetValue<string>());
        Assert.Equal("uuid", audio["properties"]!["replyId"]!["format"]!.GetValue<string>());

        // Enums as their camelCase names.
        Assert.Contains("joy", defs["Emotion"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["idle", "thinking", "speaking", "resting"], defs["FamiliarState"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));

        // Events name their payloads; cleared and resync have none.
        Assert.Equal("ReadyEvent", schema["x-events"]!["ready"]!["payload"]!.GetValue<string>());
        Assert.Equal("Error", schema["x-events"]!["turn.error"]!["constant"]!.GetValue<string>());
        Assert.Null(schema["x-events"]!["cleared"]!["payload"]);

        // Descriptions from the XML documentation.
        Assert.StartsWith("Its position in the reply", audio["properties"]!["seq"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_committed_schema_is_current()
    {
        var expected = ProtocolSchema.Generate();
        if (Environment.GetEnvironmentVariable("FAMILIAR_WRITE_PROTOCOL") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SchemaPath())!);
            File.WriteAllText(SchemaPath(), expected);
            return;
        }

        Assert.True(File.Exists(SchemaPath()), $"{SchemaPath()} is missing: run with FAMILIAR_WRITE_PROTOCOL=1.");
        var committed = File.ReadAllText(SchemaPath()).ReplaceLineEndings("\n");
        var (lines, at) = committed.Split('\n').Zip(expected.Split('\n')).Select((pair, i) => (pair, i))
            .FirstOrDefault(x => x.pair.First != x.pair.Second);
        Assert.True(committed == expected,
            "protocol/familiar.schema.json is out of date with the server's records: run the protocol tests with " +
            "FAMILIAR_WRITE_PROTOCOL=1, then `npm run protocol` in client/. " +
            $"First difference, line {at + 1}: committed `{lines.First}`, now `{lines.Second}`.");
    }
}
