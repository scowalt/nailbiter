using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Scoring;

namespace backend.Models;

public sealed class PlayoffSeriesObservation
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public required string GameId { get; init; }
    [JsonRequired] public required string Source { get; init; }
    [JsonRequired] public DateTime ObservedAt { get; init; }
    [JsonRequired] public PlayoffSeriesSnapshot? Snapshot { get; init; }

    public static PlayoffSeriesObservation? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PlayoffSeriesObservation>(json); }
        catch (JsonException) { return null; }
    }

    public static PlayoffSeriesSnapshot? TryReadFor(Game game)
    {
        var observation = Parse(game.SeriesObservationJson);
        var snapshot = observation?.Snapshot;
        return observation is { Version: 1, Source: "espn" } && observation.GameId == game.Id
            && observation.ObservedAt != default && snapshot?.IsUsableFor(game) == true ? snapshot : null;
    }
}

public sealed class PlayoffSeriesSnapshot
{
    [JsonRequired] public required string GameId { get; init; }
    [JsonRequired] public required string CompetitionId { get; init; }
    [JsonRequired] public required string Sport { get; init; }
    [JsonRequired] public int SeasonYear { get; init; }
    [JsonRequired] public int SeasonType { get; init; }
    [JsonRequired] public required string HomeTeamId { get; init; }
    [JsonRequired] public required string AwayTeamId { get; init; }
    [JsonRequired] public required string Home { get; init; }
    [JsonRequired] public required string Away { get; init; }
    [JsonRequired] public int HomeWins { get; init; }
    [JsonRequired] public int AwayWins { get; init; }
    [JsonRequired] public int BestOf { get; init; }
    [JsonRequired] public bool SeriesCompleted { get; init; }
    [JsonRequired] public required string GameStatus { get; init; }
    [JsonRequired] public bool GameCompleted { get; init; }
    public string? WinnerTeamId { get; init; }
    public string? RoundLabel { get; init; }
    public int? CurrentGameNumber { get; init; }

    public bool IsUsableFor(Game game)
    {
        if (!PlayoffSeriesCatalogue.Supports(Sport) || Sport != game.Sport || GameId != game.Id || CompetitionId != game.Id
            || SeasonYear <= 0 || SeasonYear != game.SeasonYear || SeasonType != 3 || SeasonType != game.SeasonType
            || string.IsNullOrWhiteSpace(HomeTeamId) || string.IsNullOrWhiteSpace(AwayTeamId) || HomeTeamId == AwayTeamId
            || string.IsNullOrWhiteSpace(Home) || string.IsNullOrWhiteSpace(Away) || Home == Away
            || Home != ProHypePolicy.TeamKey(Sport, game.HomeTeamAbbreviation) || Away != ProHypePolicy.TeamKey(Sport, game.AwayTeamAbbreviation)
            || (game.HomeTeamId is not null && HomeTeamId != game.HomeTeamId) || (game.AwayTeamId is not null && AwayTeamId != game.AwayTeamId)
            || BestOf is not (3 or 5 or 7) || HomeWins < 0 || AwayWins < 0
            || GameStatus != game.Status || GameStatus is not ("scheduled" or "in_progress" or "final")
            || GameCompleted != (GameStatus == "final")) return false;
        if (!PlayoffSeriesCatalogue.RoundMatches(Sport, RoundLabel, BestOf)) return false;
        var threshold = BestOf / 2 + 1;
        if (HomeWins > threshold || AwayWins > threshold || HomeWins + AwayWins > BestOf
            || (HomeWins == threshold && AwayWins == threshold)
            || SeriesCompleted != (HomeWins == threshold || AwayWins == threshold)
            || (SeriesCompleted && !GameCompleted)) return false;
        if (CurrentGameNumber is int ordinal && (ordinal < 1 || ordinal > BestOf
            || HomeWins + AwayWins != ordinal - (GameCompleted ? 0 : 1))) return false;
        if (GameCompleted && HomeWins + AwayWins == 0) return false;
        if (GameCompleted && WinnerTeamId is not null && game.HomeScore.HasValue && game.AwayScore.HasValue
            && (game.HomeScore == game.AwayScore
                || WinnerTeamId != (game.HomeScore > game.AwayScore ? HomeTeamId : AwayTeamId))) return false;
        if (WinnerTeamId is not null && (!GameCompleted || (WinnerTeamId != HomeTeamId && WinnerTeamId != AwayTeamId)
            || (WinnerTeamId == HomeTeamId ? HomeWins == 0 : AwayWins == 0)
            || (SeriesCompleted && WinnerTeamId != (HomeWins > AwayWins ? HomeTeamId : AwayTeamId)))) return false;
        return true;
    }
}
