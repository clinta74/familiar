using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Familiar.Server.Familiars;

/// <summary>What a VRM model says about itself.</summary>
/// <param name="Version">"0.x" or "1.0".</param>
/// <param name="Name">Its title, if it has one.</param>
/// <param name="Authors">Who made it.</param>
/// <param name="Licence">Its licence, as the model states it (a name or a URL).</param>
/// <param name="CommercialUse">What it says about commercial use, if anything.</param>
public sealed record VrmInfo(string Version, string? Name, IReadOnlyList<string> Authors, string? Licence, string? CommercialUse);

/// <summary>
/// Checks an upload really is a VRM model - a binary glTF with the VRM extension - and reads its
/// metadata. Only the header and the JSON chunk are read; the renderer does the rest in the browser.
/// </summary>
public static class VrmFile
{
    private const uint GlbMagic = 0x46546C67;      // "glTF"
    private const uint JsonChunk = 0x4E4F534A;     // "JSON"

    /// <summary>The model's metadata, or why it isn't a usable VRM.</summary>
    public static (VrmInfo? Info, string? Problem) Inspect(ReadOnlySpan<byte> file)
    {
        if (file.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(file) != GlbMagic)
        {
            return (null, "That isn't a VRM model (.vrm files are binary glTF).");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);

        if (version != 2 || declared != file.Length || chunkType != JsonChunk || chunkLength > file.Length - 20)
        {
            return (null, "That file is damaged or isn't a glTF 2.0 model.");
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(Encoding.UTF8.GetString(file.Slice(20, (int)chunkLength)));
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "That file is damaged or isn't a glTF 2.0 model.");
        }

        var extensions = json?["extensions"];
        if (extensions?["VRMC_vrm"]?["meta"] is JsonObject meta1)
        {
            return (new VrmInfo(
                "1.0",
                Text(meta1["name"]),
                Strings(meta1["authors"]),
                Text(meta1["licenseUrl"]),
                Text(meta1["commercialUsage"])), null);
        }

        if (extensions?["VRM"]?["meta"] is JsonObject meta0)
        {
            var author = Text(meta0["author"]);
            return (new VrmInfo(
                "0.x",
                Text(meta0["title"]),
                author is null ? [] : [author],
                Text(meta0["licenseName"]) is { } licence && licence != "Other" ? licence : Text(meta0["otherLicenseUrl"]),
                Text(meta0["commercialUssageName"])), null);
        }

        return (null, "That's a glTF model, but not a VRM one. Export it from VRoid Studio (or another VRM tool) as .vrm.");
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? (text.Length > 200 ? text[..200] : text).Trim()
            : null;

    private static string[] Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(Text).OfType<string>().Take(5)] : [];
}
