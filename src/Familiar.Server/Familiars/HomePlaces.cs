using Familiar.Agent.Tools;
using Familiar.Domain.Familiars;

namespace Familiar.Server.Familiars;

/// <summary>A place to pick as home: one of the matches for a name, or where the device is.</summary>
/// <param name="Label">How it's said and shown: "Warren, Michigan, United States".</param>
/// <param name="Name">The town: "Warren".</param>
/// <param name="Region">Its state, province or nation ("Michigan"), if it has one.</param>
/// <param name="Country">"United States".</param>
/// <param name="CountryCode">ISO 3166 alpha-2: "US".</param>
/// <param name="Latitude">Rounded to two places, about a kilometre.</param>
/// <param name="Longitude">Rounded to two places, about a kilometre.</param>
public sealed record PlaceView(string Label, string Name, string? Region, string? Country, string CountryCode, double Latitude, double Longitude)
{
    /// <summary>Coordinates kept to about a kilometre: enough for the weather, not the house.</summary>
    public static double Round(double degrees) => Math.Round(degrees, 2);

    /// <summary>"Warren, Michigan, United States", leaving out what's empty or repeated.</summary>
    public static string LabelOf(string name, string? region, string? country) =>
        string.Join(", ", new[] { name, region, country }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct());
}

/// <summary>The user's home, from their profile, as the tools and the prompt use it (BACKLOG item 32).</summary>
public static class HomePlaces
{
    /// <summary>The picked place, or null for no home or one typed in before places were picked.</summary>
    public static HomeLocation? HomeOf(FamiliarProfile profile) =>
        profile is { HomePlace: { Length: > 0 } label, HomeLatitude: { } latitude, HomeLongitude: { } longitude }
            ? new HomeLocation(label, label.Split(',')[0].Trim(), profile.HomeRegion ?? "", profile.HomeCountryCode ?? "", latitude, longitude)
            : null;

    /// <summary>The profile's home as Settings shows it; null when it isn't a picked place.</summary>
    public static PlaceView? ViewOf(FamiliarProfile profile) =>
        HomeOf(profile) is { } home
            ? new PlaceView(home.Label, home.Name, NullIfEmpty(home.Region), Country(home.Label, home.Name, home.Region), home.CountryCode, home.Latitude, home.Longitude)
            : null;

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>The label's last part, when it isn't the town or the region.</summary>
    private static string? Country(string label, string name, string region) =>
        label.Split(',').Select(s => s.Trim()).LastOrDefault() is { } last && last != name && last != region ? last : null;
}
