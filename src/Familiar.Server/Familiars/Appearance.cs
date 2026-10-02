using System.Text.Json;
using System.Text.RegularExpressions;

namespace Familiar.Server.Familiars;

/// <summary>The plush llama's colours, as #rrggbb. Shading is derived from these by the client.</summary>
/// <param name="Wool">The fleece.</param>
/// <param name="Face">The bare face and snout.</param>
/// <param name="Ear">Inside the ears, and the hooves.</param>
/// <param name="Blush">The cheeks.</param>
/// <param name="Accent">The accessory.</param>
public sealed record LlamaColours(string Wool, string Face, string Ear, string Blush, string Accent)
{
    public static readonly LlamaColours Classic = new("#fdfaf5", "#e4d6cc", "#b3a093", "#f4aab5", "#7c5cff");
}

/// <summary>
/// How a familiar looks (PLAN.md §5.5). Either the built-in plush llama, coloured and dressed, or an
/// uploaded VRM model.
/// </summary>
/// <param name="Model">"llama" or "vrm".</param>
/// <param name="VrmId">For "vrm", which uploaded model.</param>
/// <param name="Colours">The llama's colours.</param>
/// <param name="Accessory">What the llama wears: none, bow, scarf, glasses, flower.</param>
public sealed partial record AppearanceView(string Model, Guid? VrmId, LlamaColours Colours, string Accessory)
{
    public static readonly AppearanceView Default = new("llama", null, LlamaColours.Classic, "none");

    public static readonly IReadOnlyList<string> Models = ["llama", "vrm"];

    public static readonly IReadOnlyList<string> Accessories = ["none", "bow", "scarf", "glasses", "flower"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AppearanceView Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<AppearanceView>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Null when it's acceptable, otherwise why not. Only whitelisted words and #rrggbb colours get
    /// through: the client puts these into CSS.
    /// </summary>
    public string? Problem(IReadOnlyCollection<Guid> ownedModels)
    {
        if (!Models.Contains(Model))
        {
            return "Unknown model.";
        }

        if (Model == "vrm" && (VrmId is not { } id || !ownedModels.Contains(id)))
        {
            return "Choose one of your uploaded models.";
        }

        if (!Accessories.Contains(Accessory))
        {
            return "Unknown accessory.";
        }

        return Colours is null || new[] { Colours.Wool, Colours.Face, Colours.Ear, Colours.Blush, Colours.Accent }.Any(c => c is null || !Hex().IsMatch(c))
            ? "Colours are #rrggbb."
            : null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();
}
