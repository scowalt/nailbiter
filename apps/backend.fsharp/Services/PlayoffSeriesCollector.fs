namespace backend.Services

open System
open System.Collections.Generic
open System.Linq
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open backend.Scoring
open Microsoft.EntityFrameworkCore

module private PlayoffSeriesEvidence =
    let field (value: JsonElement) (name: string) =
        let mutable found = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &found) then found
        else Unchecked.defaultof<JsonElement>

    let text value name =
        let found = field value name
        if found.ValueKind = JsonValueKind.String then found.GetString() else null

    let integer value name =
        let found = field value name
        let mutable number = 0
        if found.ValueKind = JsonValueKind.Number && found.TryGetInt32(&number) then Some number else None

    let boolean value name =
        match (field value name).ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None

    let absent (value: JsonElement) = value.ValueKind = JsonValueKind.Undefined || value.ValueKind = JsonValueKind.Null
    let invalidObject value = not (absent value) && value.ValueKind <> JsonValueKind.Object
    let invalidInteger value name = not (absent (field value name)) && (integer value name).IsNone
    let invalidText value name = not (absent (field value name)) && isNull (text value name)
    let require condition = if not condition then raise (InvalidOperationException("Unusable playoff series evidence"))
    let needed value = value |> Option.defaultWith (fun () -> raise (InvalidOperationException("Missing playoff series evidence")))
    let array (value: JsonElement) = value.EnumerateArray() |> Seq.toArray
    let exactlyOne values = require (Array.length values = 1); Array.head values

    let identity participant =
        let id = text participant "id"
        let teamId = text (field participant "team") "id"
        if not (isNull id) && ((field (field participant "team") "id").ValueKind = JsonValueKind.Undefined || id = teamId) then id
        else null

    let ordinalPattern = Regex("^(.+?)(?: - Game ([1-7]))?$", RegexOptions.CultureInvariant)

    let metadata competition =
        let notes = field competition "notes"
        require (notes.ValueKind = JsonValueKind.Array || absent notes)
        let mutable round = null
        let mutable ordinal = Nullable<int>()
        if notes.ValueKind = JsonValueKind.Array then
            let notes = array notes
            require (notes |> Array.forall (fun n ->
                n.ValueKind = JsonValueKind.Object
                && ((field n "headline").ValueKind = JsonValueKind.String || absent (field n "headline"))))
            for label in notes |> Array.map (fun n -> text n "headline") |> Array.filter (isNull >> not) do
                require (not (label.Contains("Play-In", StringComparison.OrdinalIgnoreCase)))
                let matched = ordinalPattern.Match(label)
                if matched.Success && PlayoffSeriesCatalogue.RecognizesRound(matched.Groups.[1].Value) then
                    require (isNull round)
                    round <- matched.Groups.[1].Value
                    if matched.Groups.[2].Success then
                        ordinal <- Nullable(Int32.Parse(matched.Groups.[2].Value, Globalization.CultureInfo.InvariantCulture))
                elif label.Contains(" - Game", StringComparison.OrdinalIgnoreCase)
                    && PlayoffSeriesCatalogue.RecognizesRound(label.Split(" - Game").[0]) then
                    require false
        struct (round, ordinal)

    let read (game: Game) item board =
        require (PlayoffSeriesCatalogue.Supports(game.Sport))
        let leagues = field board "leagues"
        require (absent leagues || leagues.ValueKind = JsonValueKind.Array)
        let leagueItems = if leagues.ValueKind = JsonValueKind.Array then array leagues else [||]
        require (leagueItems |> Array.forall (fun league ->
            let season = field league "season"
            let phase = field season "type"
            not (invalidObject league || invalidText league "slug"
                || (not (isNull (text league "slug")) && text league "slug" <> game.Sport)
                || invalidObject season || invalidInteger season "year" || invalidObject phase || invalidInteger phase "type")))
        let season = field item "season"
        let rootSeason = field board "season"
        require ([| season; rootSeason |] |> Array.forall (fun s ->
            not (invalidObject s || invalidInteger s "year" || invalidInteger s "type" || invalidText s "slug")))
        let year = integer season "year" |> Option.orElse (integer rootSeason "year") |> needed
        let phase = integer season "type" |> Option.orElse (integer rootSeason "type") |> needed
        let agrees expected value = value |> Option.forall ((=) expected)
        require (phase = 3 && agrees year (integer rootSeason "year") && agrees phase (integer rootSeason "type")
            && ([| season; rootSeason |] |> Array.forall (fun s -> isNull (text s "slug") || text s "slug" = "post-season")))
        require (leagueItems |> Array.forall (fun league ->
            agrees year (integer (field league "season") "year") && agrees phase (integer (field (field league "season") "type") "type")))
        let competition = field item "competitions" |> array |> exactlyOne
        let series = field competition "series"
        require (text competition "id" = game.Id && text series "type" = "playoff")
        let participants = field competition "competitors" |> array
        let members = field series "competitors" |> array
        require (participants.Length = 2 && members.Length = 2)
        let home = participants |> Array.filter (fun p -> text p "homeAway" = "home") |> exactlyOne
        let away = participants |> Array.filter (fun p -> text p "homeAway" = "away") |> exactlyOne
        let homeId = identity home
        let awayId = identity away
        require (not (isNull homeId) && not (isNull awayId) && homeId <> awayId)
        let homeMember = members |> Array.filter (fun m -> text m "id" = homeId) |> exactlyOne
        let awayMember = members |> Array.filter (fun m -> text m "id" = awayId) |> exactlyOne
        let homeWins = integer homeMember "wins" |> needed
        let awayWins = integer awayMember "wins" |> needed
        let bestOf = integer series "totalCompetitions" |> needed
        let completed = boolean series "completed" |> needed
        let eventStatus = field (field item "status") "type"
        let contestStatus = field (field competition "status") "type"
        let state = text eventStatus "state"
        let gameCompleted = boolean eventStatus "completed" |> needed
        let statusAgrees status =
            let name = text status "name"
            if (field status "name").ValueKind = JsonValueKind.Undefined then state = "pre" || state = "in"
            else
                match state with
                | "pre" -> name = "STATUS_SCHEDULED"
                | "in" -> [| "STATUS_IN_PROGRESS"; "STATUS_HALFTIME"; "STATUS_END_PERIOD"; "STATUS_OVERTIME"; "STATUS_SHOOTOUT" |] |> Array.contains name
                | "post" -> name = "STATUS_FINAL" || (game.Sport <> "mlb" && (name = "STATUS_FINAL_OT" || name = "STATUS_FINAL_SO"))
                | _ -> false
        require (text contestStatus "state" = state && boolean contestStatus "completed" = Some gameCompleted
            && statusAgrees eventStatus && statusAgrees contestStatus)
        if gameCompleted then
            require ([| field item "status"; eventStatus; field competition "status"; contestStatus |] |> Array.forall (fun status ->
                [| "description"; "detail"; "shortDetail" |] |> Array.forall (fun name ->
                    let description = text status name
                    isNull description || ([| "postpon"; "suspend"; "cancel"; "delay"; "abandon" |]
                        |> Array.forall (fun word -> not (description.Contains(word, StringComparison.OrdinalIgnoreCase)))))))
        let gameStatus = match state with "pre" -> "scheduled" | "in" -> "in_progress" | "post" -> "final" | _ -> "unknown"
        let winners = participants |> Array.filter (fun p -> boolean p "winner" = Some true)
        let suppliedWinner p = (field p "winner").ValueKind <> JsonValueKind.Undefined
        require (participants |> Array.forall (fun p -> not (suppliedWinner p) || (boolean p "winner").IsSome))
        require (winners.Length <= 1 && (not gameCompleted || not (participants |> Array.exists suppliedWinner) || winners.Length = 1))
        let struct (round, ordinal) = metadata competition
        let homeAbbreviation = text (field home "team") "abbreviation"
        let awayAbbreviation = text (field away "team") "abbreviation"
        require (not (isNull homeAbbreviation) && not (isNull awayAbbreviation))
        let snapshot = PlayoffSeriesSnapshot(
            GameId = game.Id, CompetitionId = text competition "id", Sport = game.Sport,
            SeasonYear = year, SeasonType = phase, HomeTeamId = homeId, AwayTeamId = awayId,
            Home = ProHypePolicy.TeamKey(game.Sport, homeAbbreviation), Away = ProHypePolicy.TeamKey(game.Sport, awayAbbreviation),
            HomeWins = homeWins, AwayWins = awayWins, BestOf = bestOf, SeriesCompleted = completed,
            RoundLabel = round, CurrentGameNumber = ordinal, GameStatus = gameStatus, GameCompleted = gameCompleted,
            WinnerTeamId = if winners.Length = 1 then identity winners.[0] else null)
        if snapshot.IsUsableFor(game) then snapshot else null

module private PlayoffSeriesPersistence =
    let refreshFields =
        [| "Sport"; "HomeTeam"; "AwayTeam"; "HomeTeamAbbreviation"; "AwayTeamAbbreviation"; "HomeTeamId"; "AwayTeamId"
           "ScheduledStart"; "Status"; "IsOvertime"; "HomeScore"; "AwayScore"; "SeasonYear"; "SeasonType"; "WeekNumber"; "WeekLabel" |]

[<AbstractClass; Sealed>]
type PlayoffSeriesCollector private () =
    static member Observe(game: Game, board: JsonElement, observedAt: DateTime) =
        let snapshot =
            try
                let item =
                    PlayoffSeriesEvidence.field board "events" |> PlayoffSeriesEvidence.array
                    |> Array.filter (fun e -> PlayoffSeriesEvidence.text e "id" = game.Id) |> PlayoffSeriesEvidence.exactlyOne
                PlayoffSeriesEvidence.read game item board
            with
            | :? InvalidOperationException | :? KeyNotFoundException | :? FormatException -> null
        PlayoffSeriesObservation(GameId = game.Id, Source = "espn", ObservedAt = observedAt, Snapshot = snapshot)

    static member SaveAsync(db: ExcitementDbContext, observations: IEnumerable<PlayoffSeriesObservation>, suppliedSeriesIds: ISet<string>, cancellationToken: CancellationToken) : Task<HashSet<string>> =
        task {
            let rejected = HashSet<string>()
            let batch =
                observations |> Seq.groupBy (fun o -> o.GameId) |> Seq.map (fun (_, group) -> Seq.head group)
                |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a.GameId, b.GameId)) |> Seq.toArray
            if batch.Length = 0 then
                let! _ = db.SaveChangesAsync(cancellationToken)
                return rejected
            else
                use! transaction = db.Database.BeginTransactionAsync(cancellationToken)
                for observation in batch do
                    let! stored =
                        if db.Database.IsNpgsql() then
                            task {
                                let admission: FormattableString = $"SELECT pg_advisory_xact_lock(682, hashtext({observation.GameId}))"
                                let! _ = db.Database.ExecuteSqlInterpolatedAsync(admission, cancellationToken)
                                let row: FormattableString = $"SELECT * FROM games WHERE id = {observation.GameId} FOR UPDATE"
                                return! db.Games.FromSqlInterpolated(row).AsNoTracking().SingleOrDefaultAsync(cancellationToken)
                            }
                        else db.Games.AsNoTracking().SingleOrDefaultAsync((fun g -> g.Id = observation.GameId), cancellationToken)
                    let game = db.Games.Local |> Seq.find (fun g -> g.Id = observation.GameId)
                    let entry = db.Entry(game)
                    let previous = if isNull stored then null else PlayoffSeriesObservation.Parse(stored.SeriesObservationJson)
                    let superseded =
                        not (isNull stored) && stored.SeasonType = Nullable<int>(3) && not (isNull previous)
                        && (previous.ObservedAt > observation.ObservedAt || (previous.ObservedAt = observation.ObservedAt
                            && (not (isNull previous.Snapshot) || stored.Status = "canceled" || suppliedSeriesIds.Contains(game.Id))))
                    if superseded then
                        entry.CurrentValues.SetValues(stored)
                        entry.State <- EntityState.Unchanged
                        rejected.Add(game.Id) |> ignore
                    else
                        if not (isNull stored) && entry.State = EntityState.Added then
                            let desired = entry.CurrentValues.Clone()
                            entry.CurrentValues.SetValues(stored)
                            entry.State <- EntityState.Unchanged
                            for name in PlayoffSeriesPersistence.refreshFields do
                                entry.Property(name).CurrentValue <- desired.[name]
                        if entry.State <> EntityState.Added && (not (isNull observation.Snapshot)
                            || (not (isNull previous) && not (isNull previous.Snapshot))) then
                            for name in PlayoffSeriesPersistence.refreshFields do
                                entry.Property(name).IsModified <- true
                        game.SeriesObservationJson <-
                            if not (isNull previous) && previous.ObservedAt >= observation.ObservedAt then stored.SeriesObservationJson
                            else JsonSerializer.Serialize(observation)
                let! _ = db.SaveChangesAsync(cancellationToken)
                do! transaction.CommitAsync(cancellationToken)
                return rejected
        }
