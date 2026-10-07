using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Familiar.Agent.Tools;

/// <summary>JSON-schema fragments for tool parameters, kept small: every word is prompt the model reads.</summary>
public static class ToolSchema
{
    public static JsonObject Object(params (string Name, JsonObject Schema)[] properties) => Object([], properties);

    public static JsonObject Object(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var json = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0)
        {
            json["required"] = new JsonArray([.. required.Select(r => (JsonNode)JsonValue.Create(r))]);
        }

        return json;
    }

    public static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]),
    };
}

/// <summary>
/// Reads tool arguments leniently. Models send <c>"10"</c> for 10 and <c>"true"</c> for true often
/// enough that a strict reader would fail calls the user would consider perfectly clear.
/// </summary>
public static class ToolArgs
{
    public static string? String(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        } is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    public static double? Number(JsonObject arguments, string name) => arguments[name] switch
    {
        // Through the text: a value built in code from an int won't give itself up as a double.
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() == JsonValueKind.String
            && double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static bool? Bool(JsonObject arguments, string name) => arguments[name] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.String && bool.TryParse(v.GetValue<string>(), out var parsed) => parsed,
        _ => null,
    };

    /// <summary>A short user-facing label: trimmed, single-line, at most <paramref name="max"/> characters.</summary>
    public static string? Label(JsonObject arguments, string name, int max = 200)
    {
        if (String(arguments, name) is not { } text)
        {
            return null;
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max].TrimEnd();
    }
}
