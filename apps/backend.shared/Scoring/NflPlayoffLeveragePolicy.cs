using backend.Models;

namespace backend.Scoring;

public sealed record NflPlayoffPosition(ProbabilityInterval DivisionTitle, ProbabilityInterval TopSeed);
public sealed record NflPlayoffPositions(NflPlayoffPosition Win, NflPlayoffPosition Loss);

public static class NflPlayoffLeveragePolicy
{
    public const string Source = "playoffstatus_nfl_conditional_v1";
    public const string ScheduleUrl = "https://www.playoffstatus.com/nfl/nflschedule.html";
    public static readonly TimeSpan PublicationMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxKickoffDifference = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PublisherClockSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const double SeedAndNoPlayoffCellRounding = 8 * .005;

    public static bool ValidCorroboratedKickoff(Game game, NflCorroboratedKickoff? match) => match is not null &&
        !string.IsNullOrWhiteSpace(game.Id) && game.WeekNumber is >= 1 and <= 18 && match.Week == game.WeekNumber &&
        match.SourceScheduledStart.Kind == DateTimeKind.Utc && match.SourceScheduledStart != default &&
        match.EspnScheduledStart.Kind == DateTimeKind.Utc && match.EspnScheduledStart == game.ScheduledStart &&
        match.SourceScheduledStart != game.ScheduledStart &&
        (match.SourceScheduledStart - game.ScheduledStart).Duration() <= MaxKickoffDifference &&
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(match.SourceScheduledStart, Eastern)) ==
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(game.ScheduledStart, Eastern)) &&
        match.HomeGameId == game.Id && match.AwayGameId == game.Id &&
        match.HomeNextGameId == game.Id && match.AwayNextGameId == game.Id;

    public static bool ServerDated(DateTime serverDate, DateTime now) =>
        serverDate <= now + PublisherClockSkew && now - serverDate < TimeSpan.FromHours(1);

    public static bool Published(DateTime publication, DateTime observed, DateTime now) =>
        publication.Kind == DateTimeKind.Utc && publication != default && publication <= observed &&
        publication <= now && now - publication < PublicationMaxAge;

    public static NflPlayoffLeverageEvidence? Select(Game game, ProGameEvidence? context, DateTime now,
        IEnumerable<string>? knownCompletedIds = null)
    {
        var e = context?.NflPlayoffLeverage;
        if (game.Sport != "nfl" || game.SeasonType != 2 || game.SeasonYear is not int season ||
            game.Status != "scheduled" || game.IsHistoricalImport || game.ActualStart.HasValue || game.ScheduledStart <= now ||
            ProHypePolicy.ScheduledLength("nfl", season) != 17 || e is null || e.Version is not (1 or 2) || e.Source != Source ||
            e.GameId != game.Id || e.Season != season || e.Home != game.HomeTeamAbbreviation || e.Away != game.AwayTeamAbbreviation ||
            e.ScheduledStart != game.ScheduledStart || !e.ScheduleVerified || string.IsNullOrWhiteSpace(e.PublicationLabel) ||
            e.ObservedAt.Kind != DateTimeKind.Utc || !ProHypePolicy.Fresh(e.ObservedAt, now, 1) ||
            !Published(e.SchedulePublishedAt, e.ObservedAt, now) || e.CompletedGameIds is null ||
            context!.CompletedGameIds is null) return null;
        if (e.Version == 1 ? e.CorroboratedKickoff is not null : !ValidCorroboratedKickoff(game, e.CorroboratedKickoff)) return null;
        var covered = e.CompletedGameIds.ToHashSet(StringComparer.Ordinal);
        if (!context.CompletedGameIds.All(covered.Contains) || !(knownCompletedIds ?? []).All(covered.Contains)) return null;
        NflConditionalPlayoffOdds? Team(NflConditionalPlayoffOdds? odds) =>
            Valid(odds) && Published(odds!.PublishedAt, e.ObservedAt, now) &&
            Math.Abs((odds.PublishedAt - e.SchedulePublishedAt).TotalMinutes) <= 10 ? odds : null;
        var home = Team(e.HomeOdds);
        var away = Team(e.AwayOdds);
        return home is null && away is null ? null : e with { HomeOdds = home, AwayOdds = away };
    }

    public static bool Valid(NflConditionalPlayoffOdds? odds) => odds is not null &&
        odds.WinLower is double wl && odds.WinUpper is double wu && odds.LossLower is double ll && odds.LossUpper is double lu &&
        ProHypePolicy.Probability(wl) && ProHypePolicy.Probability(wu) && ProHypePolicy.Probability(ll) && ProHypePolicy.Probability(lu) &&
        wl <= wu && ll <= lu && wu >= ll &&
        Uri.TryCreate(odds.Reference, UriKind.Absolute, out var reference) &&
        reference.Scheme == "https" && reference.Host == "www.playoffstatus.com" && reference.Query == "" && reference.Fragment == "" &&
        reference.AbsolutePath.StartsWith("/nfl/", StringComparison.Ordinal) && reference.AbsolutePath.EndsWith("whatif.html", StringComparison.Ordinal);

    public static NflPlayoffPositions? Positions(NflConditionalPlayoffOdds? odds)
    {
        if (odds?.Seeds is not { } seeds || !Valid(odds)) return null;
        var win = Position(seeds.Win, odds.WinLower!.Value, odds.WinUpper!.Value);
        var loss = Position(seeds.Loss, odds.LossLower!.Value, odds.LossUpper!.Value);
        return win is null || loss is null ? null : new(win, loss);
    }

    private static NflPlayoffPosition? Position(ProbabilityInterval[]? seeds, double playoffsLower, double playoffsUpper)
    {
        if (seeds is not { Length: 7 } || seeds.Any(s => s is null || !ProHypePolicy.Probability(s.Lower) ||
            !ProHypePolicy.Probability(s.Upper) || s.Lower > s.Upper)) return null;
        if (seeds.Sum(s => s.Lower) > playoffsUpper + SeedAndNoPlayoffCellRounding ||
            seeds.Sum(s => s.Upper) < playoffsLower - SeedAndNoPlayoffCellRounding) return null;
        var top = seeds[..4];
        return new(new(Math.Min(1, top.Sum(s => s.Lower)), Math.Min(1, top.Sum(s => s.Upper))), seeds[0]);
    }

    public static (double Lower, double Upper) Stakes(NflConditionalPlayoffOdds odds, bool clinched)
    {
        var (lower, upper) = clinched ? (0d, 0d) :
            (80 * Math.Max(0, odds.WinLower!.Value - odds.LossUpper!.Value),
             80 * Math.Max(0, odds.WinUpper!.Value - odds.LossLower!.Value));
        var seeding = Positions(odds) is { } p
            ? 80 * (.5 * Proven(p.Win.DivisionTitle, p.Loss.DivisionTitle) + .5 * Proven(p.Win.TopSeed, p.Loss.TopSeed))
            : 0;
        return (Math.Min(100, lower + seeding), Math.Min(100, upper + seeding));
    }

    private static double Proven(ProbabilityInterval win, ProbabilityInterval loss) => Math.Max(0, win.Lower - loss.Upper);
}
