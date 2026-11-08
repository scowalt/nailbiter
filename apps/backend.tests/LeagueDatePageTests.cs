using System.Text.RegularExpressions;
using backend.Models;
using Xunit;

namespace backend.tests;

public sealed class LeagueDatePageDatabase : IAsyncLifetime
{
    public static readonly (string Id, DateTime Start)[] Games =
    [
        ("synthetic-jan14-late", Utc(2026, 1, 15, 4, 59)),
        ("synthetic-jan15-midnight", Utc(2026, 1, 15, 5, 0)),
        ("synthetic-jan15-late", Utc(2026, 1, 16, 4, 59)),
        ("synthetic-mar08-late", Utc(2026, 3, 9, 3, 59)),
        ("synthetic-mar09-early", Utc(2026, 3, 9, 4, 30)),
        ("synthetic-nov01-early", Utc(2026, 11, 1, 4, 0)),
        ("synthetic-nov01-late", Utc(2026, 11, 2, 4, 30)),
        ("synthetic-nov02-midnight", Utc(2026, 11, 2, 5, 0)),
    ];

    internal HostedGameFixture Host { get; } = new();

    public async Task InitializeAsync()
    {
        await Host.InitializeAsync();
        foreach (var (id, start) in Games)
            await Host.SeedAsync(new Game
            {
                Id = id, Sport = "nba", SeasonYear = 2026, SeasonType = 2, Status = "scheduled",
                HomeTeam = "Synthetic Home", AwayTeam = "Synthetic Away", HomeTeamAbbreviation = "SYH", AwayTeamAbbreviation = "SYA",
                ScheduledStart = start
            }, []);
    }

    public Task DisposeAsync() => Host.DisposeAsync();

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}

public sealed class LeagueDatePageTests(LeagueDatePageDatabase database) : IClassFixture<LeagueDatePageDatabase>
{
    [Theory]
    [InlineData("2026-01-14", "synthetic-jan14-late")]
    [InlineData("2026-01-15", "synthetic-jan15-midnight synthetic-jan15-late")]
    [InlineData("2026-03-08", "synthetic-mar08-late")]
    [InlineData("2026-03-09", "synthetic-mar09-early")]
    [InlineData("2026-11-01", "synthetic-nov01-early synthetic-nov01-late")]
    [InlineData("2026-11-02", "synthetic-nov02-midnight")]
    public async Task Date_page_lists_the_games_that_start_on_that_eastern_calendar_day(string date, string expected)
    {
        var html = await database.Host.Client.GetStringAsync($"/nba?date={date}");

        var listed = Regex.Matches(html, "href=\"/game/(synthetic-[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).Distinct().Order();
        Assert.Equal(expected.Split(' ').Order(), listed);
    }

    [Theory]
    [InlineData("2026-01-14", "synthetic-jan14-late")]
    [InlineData("2026-01-15", "synthetic-jan15-midnight synthetic-jan15-late")]
    [InlineData("2026-03-08", "synthetic-mar08-late")]
    [InlineData("2026-03-09", "synthetic-mar09-early")]
    [InlineData("2026-11-01", "synthetic-nov01-early synthetic-nov01-late")]
    [InlineData("2026-11-02", "synthetic-nov02-midnight")]
    public async Task Home_page_lists_the_games_that_start_on_the_current_eastern_calendar_day(string date, string expected)
    {
        database.Host.Clock.AdjustTime(DateTimeOffset.Parse($"{date}T17:00:00Z"));

        var html = await database.Host.Client.GetStringAsync("/");

        var listed = Regex.Matches(html, "href=\"/game/(synthetic-[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).Distinct().Order();
        Assert.Equal(expected.Split(' ').Order(), listed);
    }
}
