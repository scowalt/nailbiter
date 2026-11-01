using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using backend.Models;

namespace backend.tests;

internal sealed record SyntheticExcitementHistory(SyntheticHistoryMetadata Metadata, SyntheticHistoryContext Context, IReadOnlyList<SyntheticProbability> Samples)
{
    private const string Header = "game_id,timestamp,home_win_probability,away_win_probability,home_score,away_score,label,source,is_probability_bound";
    private static readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Excitement");

    public static SyntheticExcitementHistory Load(string gameId)
    {
        var metadata = JsonSerializer.Deserialize<SyntheticHistoryMetadata>(File.ReadAllText(Path.Combine(DirectoryPath, "context.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var context = metadata.Games.Single(game => game.GameId == gameId);
        var csvPath = Path.Combine(DirectoryPath, "histories.csv");
        if (!Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(csvPath))).Equals(metadata.SourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Synthetic history checksum changed; regenerate with scripts/test-fixtures/generate.py.");
        var lines = File.ReadAllLines(csvPath);
        if (lines[0] != Header) throw new InvalidDataException("Unexpected synthetic chart columns.");
        var rows = lines.Skip(1).Select(line => line.Split(',')).ToArray();
        if (rows.Any(row => row.Length != 9)) throw new InvalidDataException("Unexpected synthetic chart row shape.");
        var samples = rows.Where(row => row[0] == gameId).Select(row => new SyntheticProbability(
            row[0], DateTime.ParseExact(row[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            double.Parse(row[2], CultureInfo.InvariantCulture), double.Parse(row[3], CultureInfo.InvariantCulture),
            int.Parse(row[4], CultureInfo.InvariantCulture), int.Parse(row[5], CultureInfo.InvariantCulture),
            row[6], row[7], bool.Parse(row[8]))).ToArray();
        if (samples.Length != context.SampleCount || samples.Length == 0 || samples[^1].Timestamp != context.CaptureCutoff
            || samples.Any(sample => sample.Timestamp > context.CaptureCutoff)
            || samples.Zip(samples.Skip(1)).Any(pair => pair.First.Timestamp >= pair.Second.Timestamp))
            throw new InvalidDataException("Synthetic count, chronology or cutoff changed.");
        return new(metadata, context, samples);
    }

    public Game CreateGame()
    {
        var last = Samples[^1];
        var (period, clock) = last.FootballClock();
        return new Game
        {
            Id = Context.GameId, Sport = Context.Sport, HomeTeam = Context.HomeTeam, AwayTeam = Context.AwayTeam,
            HomeTeamAbbreviation = Context.HomeAbbreviation, AwayTeamAbbreviation = Context.AwayAbbreviation,
            Status = Context.Status, ScheduledStart = Samples[0].Timestamp, SeasonYear = Samples[0].Timestamp.Year,
            HomeScore = last.HomeScore, AwayScore = last.AwayScore, Period = period, GameClock = clock,
            IsHistoricalImport = Metadata.SeedingAssumptions.IsHistoricalImport
        };
    }

    public IEnumerable<OddsSnapshot> CreateSnapshots() => Samples.Select(sample =>
    {
        var (period, clock) = sample.FootballClock();
        return new OddsSnapshot
        {
            GameId = sample.GameId, Timestamp = sample.Timestamp,
            HomeWinProbability = sample.HomeWinProbability, AwayWinProbability = sample.AwayWinProbability,
            HomeScore = sample.HomeScore, AwayScore = sample.AwayScore, Period = period, GameClock = clock,
            Source = sample.Source, IsHistorical = Metadata.SeedingAssumptions.IsHistorical
        };
    });
}

internal sealed record SyntheticHistoryMetadata(string SourceArtifact, string SourceSha256, string ContextEvidence,
    string[] ObservedFields, string[] NotObservedFields, SyntheticHistorySeedAssumptions SeedingAssumptions, SyntheticHistoryContext[] Games);

internal sealed record SyntheticHistorySeedAssumptions(bool IsHistoricalImport, bool IsHistorical, string Explanation,
    string ScheduledStart, string Moneylines, string OtherFields, string Labels, string Bounds);

internal sealed record SyntheticHistoryContext(string GameId, string Sport, string HomeTeam, string AwayTeam,
    string HomeAbbreviation, string AwayAbbreviation, string Status, DateTime CaptureCutoff, int SampleCount, string Context);

internal sealed record SyntheticProbability(string GameId, DateTime Timestamp, double HomeWinProbability,
    double AwayWinProbability, int HomeScore, int AwayScore, string Label, string Source, bool IsProbabilityBound)
{
    public (int Period, string Clock) FootballClock()
    {
        var parts = Label.Split(' ');
        if (parts.Length != 2 || !parts[0].StartsWith('Q')) throw new InvalidDataException("Unexpected frozen football label: " + Label);
        return (int.Parse(parts[0][1..], CultureInfo.InvariantCulture), parts[1]);
    }
}
