using backend.Data;
using backend.GraphQL.Types;
using backend.Models;
using Backend.Rendering;
using Falco.Markup;
using Microsoft.EntityFrameworkCore;
using Microsoft.FSharp.Core;
using F1LeaderboardEntry = backend.Services.F1LeaderboardEntry;
using NascarLeaderboardEntry = backend.Services.NascarLeaderboardEntry;
using NascarRaceResultWithManufacturer = backend.Services.NascarRaceResultWithManufacturer;
using IndyCarLeaderboardEntry = backend.Services.IndyCarLeaderboardEntry;

namespace backend.Routes;

public static class MotorsportRoutes
{
    public static void MapMotorsportRoutes(this WebApplication app)
    {
        app.MapGet("/f1", HandleF1Calendar);
        app.MapGet("/f1/race/{id}", HandleF1RaceDetail);
        app.MapGet("/api/f1/race/{id}/chart", HandleF1ChartData);
        app.MapGet("/nascar", HandleNascarCalendar);
        app.MapGet("/nascar/race/{id}", HandleNascarRaceDetail);
        app.MapGet("/api/nascar/race/{id}/chart", HandleNascarChartData);
        app.MapGet("/indycar", HandleIndyCarCalendar);
        app.MapGet("/indycar/race/{id}", HandleIndyCarRaceDetail);
        app.MapGet("/api/indycar/race/{id}/chart", HandleIndyCarChartData);
    }

    private static async Task<IResult> HandleF1Calendar(
        ExcitementDbContext db,
        int? year,
        CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var races = await db.F1Races
            .Where(r => r.Year == y)
            .OrderBy(r => r.RoundNumber)
            .ToListAsync(ct);

        var content = F1CalendarRenderer.Render(y, races);
        var html = Layout.renderPage(
            $"Formula 1 {y} | Nailbiter",
            "F1 race calendar, live leaderboards, and excitement scores",
            FSharpOption<Sport>.Some(Sport.F1),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleF1RaceDetail(
        string id,
        ExcitementDbContext db,
        CancellationToken ct)
    {
        var race = await db.F1Races.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (race == null) return Results.NotFound();

        var drivers = await db.F1Drivers
            .Where(d => d.Season == race.Year)
            .OrderBy(d => d.QualifyingPosition ?? int.MaxValue)
            .ThenBy(d => d.DriverNumber)
            .ToListAsync(ct);

        var leaderboard = new List<F1LeaderboardEntry>();
        var latestTimestamp = await db.F1RaceSnapshots
            .Where(s => s.RaceId == race.Id)
            .MaxAsync(s => (DateTime?)s.Timestamp, ct);

        if (latestTimestamp != null)
        {
            var snapshots = await db.F1RaceSnapshots
                .Where(s => s.RaceId == race.Id && s.Timestamp == latestTimestamp)
                .OrderBy(s => s.Position ?? int.MaxValue)
                .ToListAsync(ct);

            var driversByAthleteId = drivers.ToDictionary(d => d.Id.ToString());

            foreach (var s in snapshots)
            {
                driversByAthleteId.TryGetValue(s.EspnAthleteId, out var driver);
                leaderboard.Add(new F1LeaderboardEntry
                {
                    EspnAthleteId = s.EspnAthleteId,
                    Position = s.Position,
                    WinProbability = s.WinProbability,
                    FullName = driver?.FullName ?? s.EspnAthleteId,
                    Abbreviation = driver?.Abbreviation,
                    TeamName = driver?.TeamName,
                    TeamColor = driver?.TeamColor,
                    HeadshotUrl = driver?.HeadshotUrl,
                    CountryCode = driver?.CountryCode
                });
            }
        }

        var hasSnapshots = await db.F1RaceSnapshots
            .AnyAsync(s => s.RaceId == race.Id, ct);

        var content = F1RaceDetailRenderer.Render(race, drivers, leaderboard, hasSnapshots);
        var title = $"{race.MeetingName} | Nailbiter";
        var html = Layout.renderPage(
            title,
            $"Live results for {race.MeetingName}",
            FSharpOption<Sport>.Some(Sport.F1),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleNascarCalendar(
        ExcitementDbContext db,
        int? year,
        CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var races = await db.NascarRaces
            .Where(r => r.Year == y)
            .OrderBy(r => r.RaceNumber)
            .ToListAsync(ct);

        var content = NascarCalendarRenderer.Render(y, races, "/logos/nascar.png", "NASCAR Cup Series", "#CF0A2C", "/nascar", 2024);
        var html = Layout.renderPage(
            $"NASCAR {y} | Nailbiter",
            "NASCAR race calendar, live leaderboards, and excitement scores",
            FSharpOption<Sport>.Some(Sport.NASCAR),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleNascarRaceDetail(
        string id,
        ExcitementDbContext db,
        CancellationToken ct)
    {
        var race = await db.NascarRaces.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (race == null) return Results.NotFound();

        var drivers = await db.NascarDrivers
            .Where(d => d.Season == race.Year)
            .OrderBy(d => d.DisplayName)
            .ToListAsync(ct);

        var driversByAthleteId = drivers.ToDictionary(d => d.EspnAthleteId);

        var leaderboard = new List<NascarLeaderboardEntry>();
        var latestTimestamp = await db.NascarRaceSnapshots
            .Where(s => s.RaceId == race.Id)
            .MaxAsync(s => (DateTime?)s.Timestamp, ct);

        if (latestTimestamp != null)
        {
            var snapshots = await db.NascarRaceSnapshots
                .Where(s => s.RaceId == race.Id && s.Timestamp == latestTimestamp)
                .OrderBy(s => s.Position ?? int.MaxValue)
                .ToListAsync(ct);

            var raceResults = await db.NascarRaceResults
                .Where(r => r.RaceId == race.Id)
                .ToDictionaryAsync(r => r.EspnAthleteId, ct);

            foreach (var s in snapshots)
            {
                driversByAthleteId.TryGetValue(s.EspnAthleteId, out var driver);
                raceResults.TryGetValue(s.EspnAthleteId, out var result);
                leaderboard.Add(new NascarLeaderboardEntry
                {
                    EspnAthleteId = s.EspnAthleteId,
                    Position = s.Position,
                    WinProbability = s.WinProbability,
                    DisplayName = driver?.DisplayName ?? result?.DisplayName ?? s.EspnAthleteId,
                    ShortName = driver?.ShortName ?? result?.ShortName ?? s.EspnAthleteId,
                    CarNumber = driver?.CarNumber ?? result?.CarNumber,
                    CountryFlagHref = driver?.CountryFlagHref ?? result?.CountryFlagHref,
                    IsOnTrack = s.IsOnTrack,
                    Manufacturer = driver?.Manufacturer
                });
            }
        }

        var results = await db.NascarRaceResults
            .Where(r => r.RaceId == race.Id)
            .OrderBy(r => r.FinishPosition ?? int.MaxValue)
            .ToListAsync(ct);

        var resultsWithMfg = results.Select(r =>
        {
            driversByAthleteId.TryGetValue(r.EspnAthleteId, out var d);
            return new NascarRaceResultWithManufacturer
            {
                EspnAthleteId = r.EspnAthleteId,
                DisplayName = r.DisplayName,
                ShortName = r.ShortName,
                FinishPosition = r.FinishPosition,
                Winner = r.Winner,
                CountryFlagHref = r.CountryFlagHref,
                CarNumber = r.CarNumber,
                StartingPosition = r.StartingPosition,
                Manufacturer = d?.Manufacturer
            };
        }).ToList();

        var startingGridResults = results
            .Where(r => r.StartingPosition != null)
            .OrderBy(r => r.StartingPosition)
            .ToList();

        var startingGrid = startingGridResults.Select(r =>
        {
            driversByAthleteId.TryGetValue(r.EspnAthleteId, out var d);
            return new NascarRaceResultWithManufacturer
            {
                EspnAthleteId = r.EspnAthleteId,
                DisplayName = r.DisplayName,
                ShortName = r.ShortName,
                FinishPosition = r.FinishPosition,
                Winner = r.Winner,
                CountryFlagHref = r.CountryFlagHref,
                CarNumber = r.CarNumber,
                StartingPosition = r.StartingPosition,
                Manufacturer = d?.Manufacturer
            };
        }).ToList();

        var hasSnapshots = await db.NascarRaceSnapshots
            .AnyAsync(s => s.RaceId == race.Id, ct);

        var content = NascarRaceDetailRenderer.Render(race, leaderboard, resultsWithMfg, startingGrid, drivers, hasSnapshots);
        var title = $"{race.RaceName} | Nailbiter";
        var html = Layout.renderPage(
            title,
            $"Live results for {race.RaceName}",
            FSharpOption<Sport>.Some(Sport.NASCAR),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleIndyCarCalendar(
        ExcitementDbContext db,
        int? year,
        CancellationToken ct)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var races = await db.IndyCarRaces
            .Where(r => r.Year == y)
            .OrderBy(r => r.RaceNumber)
            .ToListAsync(ct);

        var content = IndyCarCalendarRenderer.Render(y, races);
        var html = Layout.renderPage(
            $"IndyCar {y} | Nailbiter",
            "IndyCar race calendar, live leaderboards, and excitement scores",
            FSharpOption<Sport>.Some(Sport.INDYCAR),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleIndyCarRaceDetail(
        string id,
        ExcitementDbContext db,
        CancellationToken ct)
    {
        var race = await db.IndyCarRaces.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (race == null) return Results.NotFound();

        var drivers = await db.IndyCarDrivers
            .Where(d => d.Season == race.Year)
            .OrderBy(d => d.DisplayName)
            .ToListAsync(ct);

        var driversByAthleteId = drivers.ToDictionary(d => d.EspnAthleteId);

        var leaderboard = new List<IndyCarLeaderboardEntry>();
        var latestTimestamp = await db.IndyCarRaceSnapshots
            .Where(s => s.RaceId == race.Id)
            .MaxAsync(s => (DateTime?)s.Timestamp, ct);

        if (latestTimestamp != null)
        {
            var snapshots = await db.IndyCarRaceSnapshots
                .Where(s => s.RaceId == race.Id && s.Timestamp == latestTimestamp)
                .OrderBy(s => s.Position ?? int.MaxValue)
                .ToListAsync(ct);

            foreach (var s in snapshots)
            {
                driversByAthleteId.TryGetValue(s.EspnAthleteId, out var driver);
                leaderboard.Add(new IndyCarLeaderboardEntry
                {
                    EspnAthleteId = s.EspnAthleteId,
                    Position = s.Position,
                    WinProbability = s.WinProbability,
                    DisplayName = driver?.DisplayName ?? s.EspnAthleteId,
                    ShortName = driver?.ShortName ?? s.EspnAthleteId,
                    CarNumber = driver?.CarNumber,
                    CountryFlagHref = driver?.CountryFlagHref
                });
            }
        }

        var results = await db.IndyCarRaceResults
            .Where(r => r.RaceId == race.Id)
            .OrderBy(r => r.FinishPosition ?? int.MaxValue)
            .ToListAsync(ct);

        foreach (var result in results)
        {
            if (result.DisplayName.All(char.IsDigit) && driversByAthleteId.TryGetValue(result.EspnAthleteId, out var driver))
            {
                result.DisplayName = driver.DisplayName;
                result.ShortName = driver.ShortName;
                result.CountryFlagHref ??= driver.CountryFlagHref;
            }
        }

        var startingGrid = results
            .Where(r => r.StartingPosition != null)
            .OrderBy(r => r.StartingPosition)
            .ToList();

        var hasSnapshots = await db.IndyCarRaceSnapshots
            .AnyAsync(s => s.RaceId == race.Id, ct);

        var content = IndyCarRaceDetailRenderer.Render(race, leaderboard, results, startingGrid, drivers, hasSnapshots);
        var title = $"{race.RaceName} | Nailbiter";
        var html = Layout.renderPage(
            title,
            $"Live results for {race.RaceName}",
            FSharpOption<Sport>.Some(Sport.INDYCAR),
            content);

        return Results.Content("<!DOCTYPE html>" + html, "text/html");
    }

    private static async Task<IResult> HandleF1ChartData(string id, ExcitementDbContext db, CancellationToken ct)
    {
        var data = await db.F1RaceSnapshots
            .Where(s => s.RaceId == id && s.Position == 1)
            .OrderBy(s => s.Timestamp)
            .Select(s => new { s.Timestamp, winProbability = s.WinProbability, label = "Lap " + (s.CompletedLaps ?? 0) })
            .ToListAsync(ct);

        return Results.Ok(data);
    }

    private static async Task<IResult> HandleNascarChartData(string id, ExcitementDbContext db, CancellationToken ct)
    {
        var data = await db.NascarRaceSnapshots
            .Where(s => s.RaceId == id && s.Position == 1)
            .OrderBy(s => s.Timestamp)
            .Select(s => new { s.Timestamp, winProbability = s.WinProbability, label = "Lap " + (s.CompletedLaps ?? 0) })
            .ToListAsync(ct);

        return Results.Ok(data);
    }

    private static async Task<IResult> HandleIndyCarChartData(string id, ExcitementDbContext db, CancellationToken ct)
    {
        var data = await db.IndyCarRaceSnapshots
            .Where(s => s.RaceId == id && s.Position == 1)
            .OrderBy(s => s.Timestamp)
            .Select(s => new { s.Timestamp, winProbability = s.WinProbability, label = "Lap " + (s.CompletedLaps ?? 0) })
            .ToListAsync(ct);

        return Results.Ok(data);
    }
}
