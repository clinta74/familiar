using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Server.LookUps;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Familiar.Server.Tests;

/// <summary>BACKLOG item 33: local facts found, not guessed - a part of an article, and the areas where they live.</summary>
public sealed class LocalFactsTests
{
    private static (IHttpClientFactory Http, OutsideServices Outside) Services()
    {
        var outside = new OutsideServices();
        var services = new ServiceCollection();
        services.AddTransient<LookUpHosts>();
        services.AddHttpClient("LookUps").AddHttpMessageHandler<LookUpHosts>().ConfigurePrimaryHttpMessageHandler(() => outside);
        return (services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), outside);
    }

    private static MemoryCache Cache() => new(new MemoryCacheOptions());

    private static Weather Weather(IHttpClientFactory http) =>
        new(http, Cache(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Weather>.Instance);

    private static readonly HomeLocation Warren = new("Warren, Michigan, United States", "Warren", "Michigan", "US", 42.49, -83.01);

    private static ToolContext Context(HomeLocation? home = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow, LookUps: true, HomePlace: home?.Label, Home: home);

    [Theory]
    [InlineData("Michigan's 10th district", "District 10")]
    [InlineData("district 10 general election results", "Results")]
    [InlineData("the planning", "Planning")]
    public void A_part_is_found_by_the_words_of_its_headings(string find, string heading)
    {
        var outline = WikipediaSections.Outline(JsonNode.Parse(new OutsideServices().Sections)!["parse"]!["sections"]!.AsArray());

        Assert.Equal(heading, WikipediaSections.Best(outline, find)?.Heading);
        Assert.Null(WikipediaSections.Best(outline, "references"));
        Assert.Null(WikipediaSections.Best(outline, "weather"));
    }

    [Fact]
    public void A_part_reads_as_text_with_its_tables_a_row_to_a_line()
    {
        var html = JsonNode.Parse(new OutsideServices().SectionText)!["parse"]!["text"]!["*"]!.GetValue<string>();

        var text = WikipediaSections.Text(html);

        Assert.Contains("## District 10\nNominee | Mike Bouchard Jr. | Christina Hines\nParty | Republican | Democratic\n", text, StringComparison.Ordinal);
        Assert.Contains("The 10th district takes in Warren and Sterling Heights.", text, StringComparison.Ordinal);
        Assert.Contains("#### Nominee\n- Michael Bouchard Jr., Army captain", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[1]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[edit]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_part_is_cut_and_the_parts_after_it_kept()
    {
        var html = "<h2>District 10</h2><h4>Endorsements</h4><ul>" + string.Concat(Enumerable.Repeat("<li>Someone endorsing them</li>", 100)) +
            "</ul><h4>Results</h4><p>Bouchard 72%</p>";

        var text = WikipediaSections.Text(html);

        Assert.Contains("…", text, StringComparison.Ordinal);
        Assert.Contains("#### Results\nBouchard 72%", text, StringComparison.Ordinal);
        Assert.True(text.Length < WikipediaSections.MaxText);
    }

    [Fact]
    public void A_long_list_keeps_its_first_and_last_rows_where_the_current_holder_is()
    {
        var rows = string.Concat(Enumerable.Range(1, 40).Select(i => $"<tr><td>Senator {i}</td><td>{1900 + 2 * i}</td></tr>"));
        var html = $"<h2>List of senators</h2><table><tr><th>Senator</th><th>Took office</th></tr>{rows}</table>";

        var text = WikipediaSections.Text(html);

        Assert.Contains("Senator | Took office\nSenator 1 | 1902", text, StringComparison.Ordinal);
        Assert.Contains("…", text, StringComparison.Ordinal);
        Assert.EndsWith("Senator 40 | 1980", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Look_up_with_find_reads_that_part_and_says_what_parts_there_are_when_none_fits()
    {
        var (http, _) = Services();
        var tool = new LookUpTool(http, Cache());

        var district = await tool.InvokeAsync(Context(), new JsonObject { ["topic"] = "2026 House elections in Michigan", ["find"] = "Michigan's 10th district" }, default);
        Assert.True(district.Ok);
        Assert.Equal("District 10", district.Data["part"]!.GetValue<string>());
        Assert.Contains("Christina Hines", district.Data["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Wikipedia", district.Data["source"]!.GetValue<string>());

        var none = await tool.InvokeAsync(Context(), new JsonObject { ["topic"] = "2026 House elections in Michigan", ["find"] = "weather" }, default);
        Assert.False(none.Ok);
        Assert.Contains("Its parts are: History; Planning; District 10; Republican primary; General election", none.Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_area_names_the_districts_as_they_are_looked_up()
    {
        var (http, outside) = Services();
        var tool = new HomeAreaTool(http, Cache(), Weather(http));

        var result = await tool.InvokeAsync(Context(Warren), [], default);

        Assert.True(result.Ok);
        Assert.Equal(
            ("Michigan's 10th congressional district", "Michigan's 11th Senate district", "Michigan's 14th House of Representatives district", "Macomb County", "Warren city", "Center Line Public Schools", "48089"),
            (result.Data["congressional_district"]!.GetValue<string>(), result.Data["state_senate_district"]!.GetValue<string>(),
             result.Data["state_house_district"]!.GetValue<string>(), result.Data["county"]!.GetValue<string>(), result.Data["city"]!.GetValue<string>(),
             result.Data["school_district"]!.GetValue<string>(), result.Data["zip_area"]!.GetValue<string>()));
        Assert.Null(result.Data["township_or_city"]); // The same as the city: said once.
        Assert.Contains("about a kilometre", result.Data["accuracy"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(outside.Asked, u => u.Host == "geocoding.geo.census.gov" && u.Query.Contains("x=-83.01&y=42.49", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Home_area_is_for_the_us_and_needs_a_home()
    {
        var (http, _) = Services();
        var tool = new HomeAreaTool(http, Cache(), Weather(http));

        Assert.Contains("don't know where", (await tool.InvokeAsync(Context(), [], default)).Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
        var leeds = Warren with { Label = "Leeds, England, United Kingdom", Name = "Leeds", Region = "England", CountryCode = "GB" };
        Assert.Contains("only works in the US", (await tool.InvokeAsync(Context(leeds), [], default)).Data["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Congressional District 1", "Alaska's 1st congressional district")]
    [InlineData("Congressional District (at Large)", "Alaska's at-large congressional district")]
    [InlineData("Congressional District 12", "Alaska's 12th congressional district")]
    public void Districts_are_named_as_wikipedia_names_them(string census, string expected)
    {
        var areas = HomeAreaTool.Areas(JsonNode.Parse($$"""{"States":[{"NAME":"Alaska"}],"119th Congressional Districts":[{"NAME":"{{census}}"}]}""")!.AsObject());

        Assert.Equal(expected, areas!["congressional_district"]!.GetValue<string>());
    }
}
