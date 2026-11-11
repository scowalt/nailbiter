using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using backend.Models;
using backend.Data;
using backend.Services.Espn;
using Backend.Rendering;
using Falco.Markup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed class BroadcastNetworkTests : IAsyncLifetime
{
    private readonly PlayoffSeriesFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private static JsonNode Capture() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Broadcasts", "mlb-board.json")))!;
    private static JsonNode Board() => Capture()["board"]!["body"]!.DeepClone();
    private static IEnumerable<(string Id, string? Label)> Expected() =>
        Capture()["expectedLabels"]!.AsObject().Select(entry => (entry.Key, entry.Value?.GetValue<string>()));

    private static string? Label(JsonElement competition)
    {
        competition.TryGetProperty("broadcasts", out var broadcasts);
        competition.TryGetProperty("geoBroadcasts", out var geoBroadcasts);
        return RenderedLabel([.. EspnBroadcasts.networks(broadcasts, geoBroadcasts)]);
    }

    private static string? RenderedLabel(string[]? networks)
    {
        var game = new Game { Id = "unit", Sport = "mlb", HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY", BroadcastNetworks = networks };
        var html = string.Concat(FormatHelpers.broadcastNodes(game).Select(XmlNodeRenderer.renderNode));
        var listing = new HtmlParser().ParseDocument(html).QuerySelector(".nb-broadcast");
        return listing is null ? null : Spoken(listing)["TV: ".Length..];
    }

    private static string Spoken(INode listing) => string.Concat(listing.Descendants().Select(node => node switch
    {
        IHtmlImageElement logo => logo.AlternativeText,
        IText text => text.Data,
        _ => "",
    }));

    private async Task<AngleSharp.Dom.IDocument> Page(string path) =>
        await new HtmlParser().ParseDocumentAsync(await fixture.Host.Client.GetStringAsync(path));

    private async Task<string[]?> Stored(string id)
    {
        using var scope = fixture.Host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Games.AsNoTracking().SingleAsync(g => g.Id == id)).BroadcastNetworks;
    }

    [Fact]
    public void Synthetic_scoreboard_listings_parse_national_first_with_regional_extras_and_reject_unusable_entries()
    {
        using var document = JsonDocument.Parse(Board().ToJsonString());
        var labels = document.RootElement.GetProperty("events").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => Label(e.GetProperty("competitions")[0]));
        foreach (var (id, label) in Expected()) Assert.Equal(label, labels[id]);
    }

    [Fact]
    public void Scoreboard_deserialization_keeps_broadcasts_that_the_previous_model_discarded()
    {
        var scoreboard = JsonSerializer.Deserialize<EspnScoreboardResponse>(Board().ToJsonString())!;
        var national = scoreboard.Events.Single(e => e.Id == "synthetic-tv-regional-extras").Competitions[0];
        Assert.Equal(["ESPN+", "Fixture Sports Northeast", "Fixture Network South"], EspnBroadcasts.ofCompetition(national));
        Assert.Empty(EspnBroadcasts.ofCompetition(scoreboard.Events.Single(e => e.Id == "synthetic-tv-missing").Competitions[0]));
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "Prime Video" }, "Prime Video")]
    [InlineData(new[] { "ESPN+", "Fixture A", "Fixture B" }, "ESPN+ +2")]
    [InlineData(new[] { " ", "TNT", "" }, "TNT")]
    public void Label_is_the_primary_network_plus_a_count_of_the_others(string[] networks, string? expected) =>
        Assert.Equal(expected, RenderedLabel(networks));

    [Theory]
    [InlineData("ESPN+", "/logos/networks/espn-plus.svg")]
    [InlineData("prime video", "/logos/networks/prime-video.svg")]
    [InlineData("USA Net", "/logos/networks/usa-network.svg")]
    [InlineData("Fixture Sports Northeast", null)]
    public void Recognized_networks_render_their_logo_and_others_render_their_name(string network, string? logo)
    {
        var game = new Game { Id = "unit", Sport = "mlb", HomeTeam = "Home", AwayTeam = "Away", HomeTeamAbbreviation = "HOM", AwayTeamAbbreviation = "AWY", BroadcastNetworks = [network, "Fixture B"] };
        var html = string.Concat(FormatHelpers.broadcastNodes(game).Select(XmlNodeRenderer.renderNode));
        var listing = new HtmlParser().ParseDocument(html).QuerySelector(".nb-broadcast")!;
        Assert.Equal(logo, listing.QuerySelector("img.network-logo")?.GetAttribute("src"));
        Assert.Equal($"TV: {network} +1", Spoken(listing));
    }

    [Fact]
    public async Task Every_network_logo_is_served_as_an_svg()
    {
        foreach (var url in Logos.networkLogos.Values.Distinct())
        {
            using var response = await fixture.Host.Client.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, url);
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Collected_networks_persist_and_render_beside_the_start_time_on_home_league_and_game_pages()
    {
        fixture.Provider.Board = Board();
        await fixture.CollectAsync();

        using var home = await Page("/");
        using var league = await Page("/mlb?date=2026-10-01");
        foreach (var (id, label) in Expected())
        {
            foreach (var page in new[] { home, league })
            {
                var card = page.QuerySelector($"a[href='/game/{id}']");
                Assert.NotNull(card);
                var rendered = card!.QuerySelector($"#broadcast-{id}");
                if (label is null) Assert.Null(rendered);
                else Assert.Equal($"TV: {label}", Spoken(rendered!));
                Assert.DoesNotContain("televised", card.TextContent, StringComparison.OrdinalIgnoreCase);
            }
            using var detail = await Page($"/game/{id}");
            var detailListing = detail.QuerySelector($"#broadcast-{id}");
            Assert.Equal(label is null ? null : $"TV: {label}", detailListing is null ? null : Spoken(detailListing));
        }
        Assert.Equal(["Prime Video"], (await Stored("synthetic-tv-national"))!);
        Assert.Null(await Stored("synthetic-tv-missing"));
        Assert.Contains("TV: ESPN+, Fixture Sports Northeast, Fixture Network South",
            home.QuerySelector("#broadcast-synthetic-tv-regional-extras")!.GetAttribute("title"));
        Assert.Equal("Prime Video", home.QuerySelector("#broadcast-synthetic-tv-national img.network-logo")!.GetAttribute("alt"));
        Assert.Null(home.QuerySelector("#broadcast-synthetic-tv-geo-fallback img"));
    }

    [Fact]
    public async Task A_later_listing_without_networks_keeps_the_last_known_networks_and_a_new_listing_replaces_them()
    {
        fixture.Provider.Board = Board();
        await fixture.CollectAsync();

        var changed = Board();
        foreach (var item in changed["events"]!.AsArray())
        {
            var competition = item!["competitions"]![0]!.AsObject();
            if (item["id"]!.GetValue<string>() == "synthetic-tv-national") competition.Remove("broadcasts");
            if (item["id"]!.GetValue<string>() == "synthetic-tv-geo-fallback")
                competition["broadcasts"] = JsonNode.Parse("""[{"market":"national","names":["Fixture Replacement"]}]""");
        }
        fixture.Provider.Board = changed;
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.CollectAsync();

        Assert.Equal(["Prime Video"], (await Stored("synthetic-tv-national"))!);
        Assert.Equal(["Fixture Replacement"], (await Stored("synthetic-tv-geo-fallback"))!);
    }

    [Fact]
    public async Task Completed_games_do_not_show_where_to_watch()
    {
        fixture.Provider.Board = Board();
        await fixture.CollectAsync();
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            var game = await db.Games.SingleAsync(g => g.Id == "synthetic-tv-national");
            game.Status = "final";
            await db.SaveChangesAsync();
        }
        using var detail = await Page("/game/synthetic-tv-national");
        Assert.Null(detail.QuerySelector("#broadcast-synthetic-tv-national"));
        Assert.NotNull(await Stored("synthetic-tv-national"));
    }

    [Fact]
    public async Task Migration_adds_the_nullable_column_and_is_repeatable()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "038_GameBroadcasts.sql"));
        using (var scope = fixture.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE games DROP COLUMN broadcast_networks");
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        fixture.Provider.Board = Board();
        await fixture.CollectAsync();
        Assert.Equal(["TBS", "Fixture Sports Lakes"], (await Stored("synthetic-tv-geo-fallback"))!);
    }
}
