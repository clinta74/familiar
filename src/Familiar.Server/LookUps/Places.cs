using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Server.Familiars;

namespace Familiar.Server.LookUps;

/// <summary>
/// Finding a place to pick as home in Settings (BACKLOG item 32): the matches for a name, so "Warren"
/// can be Warren, Michigan rather than whichever comes first; and the town at some coordinates, for
/// "use my current location". Only Settings calls these, never the model.
/// </summary>
public sealed class Places(IHttpClientFactory http)
{
    /// <summary>At most this many matches for a name.</summary>
    public const int MaxMatches = 8;

    /// <summary>Places called [name] (Open-Meteo's geocoding), most populous first, as Open-Meteo orders them.</summary>
    public async Task<IReadOnlyList<PlaceView>> SearchAsync(string name, CancellationToken ct)
    {
        using var response = await http.CreateClient(nameof(LookUps)).GetAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count={MaxMatches}&language=en&format=json", ct);
        response.EnsureSuccessStatusCode();
        var results = (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["results"] as JsonArray)?.OfType<JsonObject>() ?? [];
        return [.. results
            .Select(r => Place(Text(r, "name"), Text(r, "admin1"), Text(r, "country"), Text(r, "country_code"),
                r["latitude"]?.GetValue<double>(), r["longitude"]?.GetValue<double>()))
            .OfType<PlaceView>()
            .DistinctBy(p => p.Label)];
    }

    /// <summary>
    /// The town at [latitude], [longitude] (OpenStreetMap's Nominatim, at town level). The coordinates
    /// kept are the device's, to a kilometre - closer than the town's centre - not Nominatim's.
    /// </summary>
    public async Task<PlaceView?> AtAsync(double latitude, double longitude, CancellationToken ct)
    {
        using var response = await http.CreateClient(nameof(LookUps)).GetAsync(string.Create(CultureInfo.InvariantCulture,
            $"https://nominatim.openstreetmap.org/reverse?lat={PlaceView.Round(latitude)}&lon={PlaceView.Round(longitude)}&zoom=10&format=jsonv2&accept-language=en"), ct);
        response.EnsureSuccessStatusCode();
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["address"] is not JsonObject address)
        {
            return null;
        }

        var town = new[] { "city", "town", "village", "municipality", "hamlet", "suburb", "county" }
            .Select(key => Text(address, key)).FirstOrDefault(t => t.Length > 0) ?? "";
        return Place(town, Text(address, "state"), Text(address, "country"), Text(address, "country_code"), latitude, longitude);
    }

    private static PlaceView? Place(string name, string region, string country, string code, double? latitude, double? longitude)
    {
        name = LookUpText.Clean(name, 60);
        region = LookUpText.Clean(region, 60);
        country = LookUpText.Clean(country, 60);
        if (name.Length == 0 || latitude is not { } lat || longitude is not { } lon)
        {
            return null;
        }

        return new PlaceView(PlaceView.LabelOf(name, region, country), name, region.Length > 0 ? region : null, country.Length > 0 ? country : null,
            code.ToUpperInvariant(), PlaceView.Round(lat), PlaceView.Round(lon));
    }

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
