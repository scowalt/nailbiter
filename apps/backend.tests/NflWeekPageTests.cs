using backend.Data;
using backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.tests;

public sealed partial class NflPlayoffPageTests
{
    [Fact]
    public async Task Nfl_default_week_keeps_remaining_games_instead_of_jumping_to_next_week()
    {
        clock.Now = new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var remaining = NflWeekGame("week-5-remaining", 5, new DateTime(2026, 10, 13, 0, 15, 0, DateTimeKind.Utc));
        var next = NflWeekGame("week-6-upcoming", 6, new DateTime(2026, 10, 16, 0, 15, 0, DateTimeKind.Utc));
        db.Games.AddRange(remaining, next);
        await db.SaveChangesAsync();

        await AssertNflWeekAsync("/nfl", 5, remaining.Id);

        clock.Now = new DateTimeOffset(remaining.ScheduledStart.AddMinutes(30));
        remaining.Status = "in_progress";
        await db.SaveChangesAsync();
        await AssertNflWeekAsync("/nfl", 5, remaining.Id);

        remaining.Status = "final";
        await db.SaveChangesAsync();
        await AssertNflWeekAsync("/nfl", 6, next.Id);
        await AssertNflWeekAsync("/nfl?week=5&season=2026&type=2", 5, remaining.Id);
        await AssertNflWeekAsync("/nfl?week=6&season=2026&type=2", 6, next.Id);
    }

    [Fact]
    public async Task Nfl_default_week_includes_the_whole_week_and_preserves_navigation()
    {
        clock.Now = new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(
            NflWeekGame("week-4-final", 4, clock.Now.UtcDateTime.AddDays(-7), "final"),
            NflWeekGame("week-5-thursday", 5, clock.Now.UtcDateTime.AddDays(-3), "final"),
            NflWeekGame("week-5-sunday", 5, clock.Now.UtcDateTime.AddHours(1)),
            NflWeekGame("week-5-monday", 5, clock.Now.UtcDateTime.AddDays(1)),
            NflWeekGame("week-6-thursday", 6, clock.Now.UtcDateTime.AddDays(4)),
            NflWeekGame("week-6-sunday", 6, clock.Now.UtcDateTime.AddDays(7)));
        await db.SaveChangesAsync();

        await AssertNflWeekAsync("/nfl", 5, "week-5-thursday", "week-5-sunday", "week-5-monday");
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync("/nfl"), CancellationToken.None);
        var previous = document.QuerySelector("a[href='/nfl?week=4&season=2026&type=2']");
        var next = document.QuerySelector("a[href='/nfl?week=6&season=2026&type=2']");
        Assert.NotNull(previous);
        Assert.NotNull(next);
        await AssertNflWeekAsync(previous.GetAttribute("href")!, 4, "week-4-final");
        await AssertNflWeekAsync(next.GetAttribute("href")!, 6, "week-6-thursday", "week-6-sunday");
    }

    [Fact]
    public async Task Nfl_default_week_ignores_canceled_and_ungrouped_upcoming_games()
    {
        clock.Now = new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var ungrouped = NflWeekGame("ungrouped", 6, clock.Now.UtcDateTime.AddHours(2));
        ungrouped.WeekNumber = null;
        db.Games.AddRange(
            NflWeekGame("canceled", 6, clock.Now.UtcDateTime.AddHours(1), "canceled"),
            ungrouped,
            NflWeekGame("week-5-remaining", 5, clock.Now.UtcDateTime.AddDays(1)),
            NflWeekGame("week-6-upcoming", 6, clock.Now.UtcDateTime.AddDays(4)));
        await db.SaveChangesAsync();

        await AssertNflWeekAsync("/nfl", 5, "week-5-remaining");
    }

    [Fact]
    public async Task Nfl_default_week_retains_recent_finals_when_no_upcoming_games_are_known()
    {
        clock.Now = new DateTimeOffset(2026, 10, 14, 16, 0, 0, TimeSpan.Zero);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        db.Games.AddRange(
            NflWeekGame("week-4-final", 4, clock.Now.UtcDateTime.AddDays(-7), "final"),
            NflWeekGame("week-5-final", 5, clock.Now.UtcDateTime.AddDays(-1), "final"));
        await db.SaveChangesAsync();

        await AssertNflWeekAsync("/nfl", 5, "week-5-final");
    }

    [Fact]
    public async Task Nfl_default_week_fallback_ignores_canceled_and_ungrouped_games()
    {
        clock.Now = new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>();
        var ungrouped = NflWeekGame("ungrouped", 6, clock.Now.UtcDateTime.AddDays(-9));
        ungrouped.WeekNumber = null;
        db.Games.AddRange(
            NflWeekGame("week-5-final", 5, clock.Now.UtcDateTime.AddDays(-11), "final"),
            NflWeekGame("canceled", 6, clock.Now.UtcDateTime.AddDays(-10), "canceled"),
            ungrouped);
        await db.SaveChangesAsync();

        await AssertNflWeekAsync("/nfl", 5, "week-5-final");
    }

    private static Game NflWeekGame(string id, int week, DateTime start, string status = "scheduled") => new()
    {
        Id = id, Sport = "nfl", HomeTeam = "Kansas City Chiefs", AwayTeam = "Buffalo Bills",
        HomeTeamAbbreviation = "KC", AwayTeamAbbreviation = "BUF", SeasonYear = 2026,
        SeasonType = 2, WeekNumber = week, WeekLabel = $"Week {week}", Status = status,
        ScheduledStart = start
    };

    private async Task AssertNflWeekAsync(string path, int week, params string[] gameIds)
    {
        using var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(
            await client.GetStringAsync(path), CancellationToken.None);
        Assert.Equal($"2026 Week {week}", document.QuerySelector("h1 + p")!.TextContent);
        Assert.Equal(gameIds.Select(id => $"/game/{id}").OrderBy(href => href),
            document.QuerySelectorAll("a[href^='/game/']").Select(link => link.GetAttribute("href")!).OrderBy(href => href));
    }
}
