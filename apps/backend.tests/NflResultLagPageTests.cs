using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests
{
    [Fact]
    public async Task Night_game_keeps_its_hype_while_the_source_has_not_yet_published_an_earlier_final()
    {
        var kickoff = new DateTime(2026, 10, 19, 0, 20, 0, DateTimeKind.Utc);
        var game = NflPlayoffLeverageTests.Game(); game.Id = "control-7-DET-GB"; game.ScheduledStart = kickoff;
        var schedules = Rewrite(NflPlayoffLeverageTests.Schedules(), game.Id, e => e["date"] = kickoff);
        var source = new NflPlayoffLeverageTests.Handler
        {
            Transform = (path, body) => path.EndsWith("nflschedule.html")
                ? body.Replace("<a href=\"#packers\">GB</a></td><td>Sun Oct 18, 1:00 pm", "<a href=\"#packers\">GB</a></td><td>Sun Oct 18, 8:20 pm")
                : body
        };
        var boardEvent = JsonNode.Parse(schedules.Single(s => s.Team == "DET").Events.Single(e => e.GetProperty("id").GetString() == game.Id).GetRawText())!;
        boardEvent["season"]!["type"] = 2;
        var espn = new NflScheduleWire(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath.EndsWith("/teams"))
                return JsonSerializer.Serialize(new { sports = new[] { new { leagues = new[] { new { season = new { year = 2026 }, teams = schedules.Select(s => new { team = new { id = s.Team, abbreviation = s.Team } }) } } } } });
            if (url.AbsolutePath.EndsWith("/schedule"))
            {
                var team = url.Segments[^2].TrimEnd('/');
                return JsonSerializer.Serialize(new { status = "success", timestamp = clock.Now.UtcDateTime, season = new { year = 2026, type = 2 }, team = new { abbreviation = team, groups = new { id = "controlled-division" } }, events = schedules.Single(s => s.Team == team).Events });
            }
            return JsonSerializer.Serialize(new { events = new[] { boardEvent } });
        });
        using var wired = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddHttpClient<NflPlayoffLeverageCollector>().ConfigurePrimaryHttpMessageHandler(() => source);
            services.RemoveAll<ProCompetitionEvidenceService>();
            services.AddScoped(sp => new ProCompetitionEvidenceService(new HttpClient(espn, disposeHandler: false),
                sp.GetRequiredService<ExcitementDbContext>(), clock, NullLogger<ProCompetitionEvidenceService>.Instance,
                sp.GetRequiredService<NflPlayoffLeverageCollector>()));
        }));

        clock.Now = new DateTimeOffset(2026, 10, 18, 16, 0, 0, TimeSpan.Zero);
        source.ServerDate = clock.Now.UtcDateTime;
        double? captured;
        using (var scope = wired.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
            db.Games.Add(game);
            db.TeamRatings.AddRange(NflPlayoffLeverageTests.Ranks(game).Select(r => { r.FetchedAt = clock.Now.UtcDateTime; return r; }));
            var odds = NflPlayoffLeverageTests.Odds(game); odds.FetchedAt = clock.Now.UtcDateTime;
            db.PregameOdds.Add(odds);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
            await lifecycle.CaptureAsync();
            captured = (await lifecycle.ResolveAsync(game.Id)).Total;
        }
        Assert.Equal(59, Math.Round(captured!.Value));

        clock.Now = new DateTimeOffset(2026, 10, 18, 20, 30, 0, TimeSpan.Zero);
        source.ServerDate = clock.Now.UtcDateTime;
        schedules = Rewrite(schedules, "control-7-PIT-ARI", e =>
        {
            e["competitions"]![0]!["status"] = JsonNode.Parse("""{"type":{"name":"STATUS_FINAL","state":"post","completed":true},"period":4}""");
            e["competitions"]![0]!["competitors"]![0]!["score"]!["value"] = "24";
            e["competitions"]![0]!["competitors"]![1]!["score"]!["value"] = "17";
        });
        using (var scope = wired.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProCompetitionEvidenceService>().RefreshAsync(default);
            var lifecycle = scope.ServiceProvider.GetRequiredService<IProHypeLifecycle>();
            await lifecycle.CaptureAsync();
            Assert.Equal(captured, (await lifecycle.ResolveAsync(game.Id)).Total);
        }
        using var browser = wired.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var doc = await new HtmlParser().ParseDocumentAsync(await browser.GetStringAsync("/nfl?week=7&season=2026&type=2"));
        var card = doc.QuerySelector($"a[href='/game/{game.Id}']")!;
        Assert.DoesNotContain("Hype unavailable", card.TextContent);
        Assert.Matches(@"Hype\s*59", card.TextContent);
    }

    private static ProCompetitionEvidenceService.Schedule[] Rewrite(ProCompetitionEvidenceService.Schedule[] schedules, string id, Action<JsonNode> change) =>
        schedules.Select(s =>
        {
            var events = s.Events.Select(e =>
            {
                if (e.GetProperty("id").GetString() != id) return e;
                var copy = JsonNode.Parse(e.GetRawText())!;
                change(copy);
                return JsonSerializer.SerializeToElement(copy);
            }).ToArray();
            return s with { Events = events };
        }).ToArray();
}
