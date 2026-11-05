using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Services.Espn;
using Backend.Rendering;
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
        return FormatHelpers.broadcastLabel([.. EspnBroadcasts.networks(broadcasts, geoBroadcasts)])?.Value;
    }

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
        Assert.Equal(expected, FormatHelpers.broadcastLabel(networks)?.Value);

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
                else Assert.Equal($"TV: {label}", rendered!.TextContent);
                Assert.DoesNotContain("televised", card.TextContent, StringComparison.OrdinalIgnoreCase);
            }
            using var detail = await Page($"/game/{id}");
            Assert.Equal(label is null ? null : $"TV: {label}", detail.QuerySelector($"#broadcast-{id}")?.TextContent);
        }
        Assert.Equal(["Prime Video"], (await Stored("synthetic-tv-national"))!);
        Assert.Null(await Stored("synthetic-tv-missing"));
        Assert.Contains("TV: ESPN+, Fixture Sports Northeast, Fixture Network South",
            home.QuerySelector("#broadcast-synthetic-tv-regional-extras")!.GetAttribute("title"));
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
