using System.ComponentModel.DataAnnotations;
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
/// <param name="Model">A built-in character (llama, fox, hedgehog, monster, otter, giraffe, spot) or "vrm".</param>
/// <param name="VrmId">For "vrm", which uploaded model.</param>
/// <param name="Colours">The character's colours (what each paints depends on the character).</param>
/// <param name="Accessory">What it wears: none, bow, scarf, glasses, flower. The spot wears nothing.</param>
/// <param name="Style">How a built-in character is drawn: "2d" (flat, the default) or "3d" (modelled and lit).</param>
public sealed partial record AppearanceView(
    [property: AllowedValues("llama", "fox", "hedgehog", "monster", "otter", "giraffe", "spot", "vrm")] string Model,
    Guid? VrmId,
    LlamaColours Colours,
    [property: AllowedValues("none", "bow", "scarf", "glasses", "flower")] string Accessory,
    [property: AllowedValues("2d", "3d")] string Style = "2d")
{
    public static readonly AppearanceView Default = new("llama", null, LlamaColours.Classic, "none");

    public static readonly IReadOnlyList<string> Models = ["llama", "fox", "hedgehog", "monster", "otter", "giraffe", "spot", "vrm"];

    public static readonly IReadOnlyList<string> Accessories = ["none", "bow", "scarf", "glasses", "flower"];

    public static readonly IReadOnlyList<string> Styles = ["2d", "3d"];

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
    /// The sentence in the persona saying what the familiar looks like, so it knows what it is (the
    /// model denied being a llama until told). Fixed phrases per model, never user text. Empty for an
    /// uploaded model: it could be anything.
    /// </summary>
    public string Look() => Model switch
    {
        "fox" => "You look like a soft plush fox with big pointed ears and a bushy tail, and you're happy being one.",
        "hedgehog" => "You look like a round plush hedgehog with soft prickles and a little button nose, and you're happy being one.",
        "monster" => "You look like a small, fuzzy, friendly monster with big googly eyes and stubby horns, and you're happy being one.",
        "otter" => "You look like a sleek plush otter with a big whiskery muzzle and a thick tail, and you're happy being one.",
        "giraffe" => "You look like a plush giraffe with a long neck, a patchwork coat, little horns and long lashes, and you're happy being one.",
        "spot" => "You look like a soft spot of colour with no face: you show how you feel by changing your shape, your surface and your colour, and you're happy being one.",
        "vrm" => "",
        _ => Familiar.Agent.Prompting.PromptBuilder.DefaultLook,
    };

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

        if (!Styles.Contains(Style))
        {
            return "Style is 2d or 3d.";
        }

        return Colours is null || new[] { Colours.Wool, Colours.Face, Colours.Ear, Colours.Blush, Colours.Accent }.Any(c => c is null || !Hex().IsMatch(c))
            ? "Colours are #rrggbb."
            : null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();
}
