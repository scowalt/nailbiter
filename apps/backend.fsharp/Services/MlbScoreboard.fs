namespace backend.Services

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open backend.Data
open backend.Models
open Backend.FSharp
open Microsoft.EntityFrameworkCore

[<CLIMutable>]
type MlbCountReport = { Balls: Nullable<int>; Strikes: Nullable<int>; Outs: Nullable<int> }

[<CLIMutable>]
type MlbBaseReport = { Occupied: Nullable<bool>; Runner: string }

[<CLIMutable>]
type MlbBasesReport = { First: MlbBaseReport; Second: MlbBaseReport; Third: MlbBaseReport }

[<CLIMutable>]
type MlbMatchupReport = { Batter: string; Pitcher: string }

[<CLIMutable>]
type MlbTeamLineReport = { Runs: Nullable<int>; Hits: Nullable<int>; Errors: Nullable<int>; Innings: Nullable<int>[] }

[<CLIMutable>]
type MlbScoreboardReport =
    { GameId: string
      CompetitionId: string
      Provider: string
      Provenance: string
      HomeTeamId: string
      AwayTeamId: string
      State: string
      Detail: string
      ObservedAt: Nullable<DateTimeOffset>
      SourceRevisionAt: Nullable<DateTimeOffset>
      Inning: Nullable<int>
      Half: string
      Count: MlbCountReport
      Bases: MlbBasesReport
      Matchup: MlbMatchupReport
      Away: MlbTeamLineReport
      Home: MlbTeamLineReport }

[<RequireQualifiedAccess>]
type MlbSide =
    | Home
    | Away

[<RequireQualifiedAccess>]
type MlbProvenance =
    | Live
    | Recorded

[<RequireQualifiedAccess>]
type MlbGameState =
    | Scheduled
    | InProgress
    | Delayed
    | Suspended
    | Postponed
    | Canceled
    | Final

[<RequireQualifiedAccess>]
type MlbHalf =
    | Top
    | Bottom

[<RequireQualifiedAccess>]
type MlbBase =
    | Empty
    | Occupied of runner: string option

type MlbBases = { First: MlbBase; Second: MlbBase; Third: MlbBase }

type MlbCount = { Balls: int; Strikes: int; Outs: int }

type MlbMatchup = { Batter: string option; Pitcher: string option }

type MlbTeamLine = { Runs: int; Hits: int option; Errors: int option; Innings: int option list option }

[<RequireQualifiedAccess>]
type MlbScoreboardRejection =
    | NotMlb
    | WrongGame
    | WrongCompetition
    | MissingTeamIdentity
    | WrongTeam of side: MlbSide
    | MissingScore of side: MlbSide
    | MissingDetail
    | Impossible of field: string
    | FutureObservation
    | RevisionAfterObservation
    | RegressingObservation
    | RegressingRevision
    | ProviderChanged

type MlbScoreboardFrame =
    private
        { Report: MlbScoreboardReport
          ObservedAt: DateTime
          SourceRevisionAt: DateTime option
          Provider: string
          Provenance: MlbProvenance
          State: MlbGameState
          Detail: string
          Inning: int option
          Half: MlbHalf option
          Count: MlbCount option
          Bases: MlbBases option
          Matchup: MlbMatchup option
          AwayLine: MlbTeamLine
          HomeLine: MlbTeamLine }

[<RequireQualifiedAccess>]
type MlbScoreboardPhase =
    | Unavailable
    | Pregame
    | Live
    | Stale
    | Recorded
    | Delayed
    | Suspended
    | Postponed
    | Canceled
    | Final

[<RequireQualifiedAccess>]
type MlbLineCell =
    | Runs of int
    | Missing
    | NotPlayed

type MlbTeamScore = { Runs: int; Hits: int option; Errors: int option; Line: MlbLineCell list }

type MlbScoreboardBoard =
    { Away: MlbTeamScore
      Home: MlbTeamScore
      Innings: int
      Detail: string
      Inning: int option
      ObservedAt: DateTime }

type MlbActiveContext =
    { Half: MlbHalf option
      Count: MlbCount option
      Bases: MlbBases option
      Matchup: MlbMatchup option }

type MlbScoreboardView =
    { Phase: MlbScoreboardPhase
      Board: MlbScoreboardBoard option
      Active: MlbActiveContext option
      Completed: bool }

module MlbScoreboard =
    let ActiveContextMaxAge = TimeSpan.FromMinutes 2.0

    type private ResultBuilder() =
        member _.Bind(result: Result<'T, 'E>, next: 'T -> Result<'U, 'E>) = Result.bind next result
        member _.Return(value: 'T) : Result<'T, 'E> = Ok value
        member _.ReturnFrom(result: Result<'T, 'E>) = result

    let private result = ResultBuilder()

    let private require condition rejection = if condition then Ok() else Error rejection

    let private orReject rejection value =
        match value with
        | Some present -> Ok present
        | None -> Error rejection

    let private text (value: string) = if String.IsNullOrWhiteSpace value then None else Some value

    let private impossible field = MlbScoreboardRejection.Impossible field

    let private nonNegative (value: Nullable<int>) = not value.HasValue || value.Value >= 0

    let private state value =
        match value with
        | "scheduled" -> Ok MlbGameState.Scheduled
        | "in_progress" -> Ok MlbGameState.InProgress
        | "delayed" -> Ok MlbGameState.Delayed
        | "suspended" -> Ok MlbGameState.Suspended
        | "postponed" -> Ok MlbGameState.Postponed
        | "canceled" -> Ok MlbGameState.Canceled
        | "final" -> Ok MlbGameState.Final
        | _ -> Error(impossible "state")

    let private provenance value =
        match value with
        | "live" -> Ok MlbProvenance.Live
        | "recorded" -> Ok MlbProvenance.Recorded
        | _ -> Error(impossible "provenance")

    let private half (inning: int option) value =
        match text value, inning with
        | None, _ -> Ok None
        | Some "top", Some _ -> Ok(Some MlbHalf.Top)
        | Some "bottom", Some _ -> Ok(Some MlbHalf.Bottom)
        | _ -> Error(impossible "half")

    let private count (report: MlbCountReport) =
        let within (value: Nullable<int>) high field =
            if value.HasValue && value.Value >= 0 && value.Value <= high then Ok value.Value else Error(impossible field)
        match ofObj report with
        | None -> Ok None
        | Some report ->
            result {
                let! balls = within report.Balls 3 "count.balls"
                let! strikes = within report.Strikes 2 "count.strikes"
                let! outs = within report.Outs 3 "count.outs"
                return Some { Balls = balls; Strikes = strikes; Outs = outs }
            }

    let private baseOf field (report: MlbBaseReport) =
        match ofObj report with
        | Some report when report.Occupied.HasValue && report.Occupied.Value -> Ok(MlbBase.Occupied(text report.Runner))
        | Some report when report.Occupied.HasValue && (text report.Runner).IsNone -> Ok MlbBase.Empty
        | _ -> Error(impossible field)

    let private bases (report: MlbBasesReport) =
        match ofObj report with
        | None -> Ok None
        | Some report ->
            result {
                let! first = baseOf "bases.first" report.First
                let! second = baseOf "bases.second" report.Second
                let! third = baseOf "bases.third" report.Third
                return Some { First = first; Second = second; Third = third }
            }

    let private line side (report: MlbTeamLineReport) : Result<MlbTeamLine, MlbScoreboardRejection> =
        let field name = (match side with MlbSide.Home -> "home." | MlbSide.Away -> "away.") + name
        match ofObj report with
        | Some report when report.Runs.HasValue ->
            let innings = ofObj report.Innings |> Option.map (Seq.map ofNullable >> List.ofSeq)
            let recorded = innings |> Option.defaultValue [] |> List.choose id
            result {
                do! require (report.Runs.Value >= 0) (impossible (field "runs"))
                do! require (nonNegative report.Hits) (impossible (field "hits"))
                do! require (nonNegative report.Errors) (impossible (field "errors"))
                do! require (recorded |> List.forall (fun runs -> runs >= 0)) (impossible (field "innings"))
                do! require (List.sum recorded <= report.Runs.Value) (impossible (field "innings"))
                return { Runs = report.Runs.Value; Hits = ofNullable report.Hits; Errors = ofNullable report.Errors; Innings = innings }
            }
        | _ -> Error(MlbScoreboardRejection.MissingScore side)

    let private teams (game: Game) (report: MlbScoreboardReport) =
        result {
            do! require ([ report.HomeTeamId; report.AwayTeamId; game.HomeTeamId; game.AwayTeamId ] |> List.forall (text >> Option.isSome))
                    MlbScoreboardRejection.MissingTeamIdentity
            do! require (report.HomeTeamId = game.HomeTeamId) (MlbScoreboardRejection.WrongTeam MlbSide.Home)
            do! require (report.AwayTeamId = game.AwayTeamId) (MlbScoreboardRejection.WrongTeam MlbSide.Away)
            return! require (report.HomeTeamId <> report.AwayTeamId) (impossible "awayTeamId")
        }

    let validate (game: Game) (report: MlbScoreboardReport) : Result<MlbScoreboardFrame, MlbScoreboardRejection> =
        result {
            do! require (game.Sport = "mlb") MlbScoreboardRejection.NotMlb
            let! report = ofObj report |> orReject MlbScoreboardRejection.WrongGame
            do! require (report.GameId = game.Id) MlbScoreboardRejection.WrongGame
            do! require (report.CompetitionId = game.Id) MlbScoreboardRejection.WrongCompetition
            let! provider = text report.Provider |> orReject (impossible "provider")
            let! provenance = provenance report.Provenance
            do! teams game report
            let! away = line MlbSide.Away report.Away
            let! home = line MlbSide.Home report.Home
            let! state = state report.State
            let! detail = text report.Detail |> orReject MlbScoreboardRejection.MissingDetail
            let! observedAt = ofNullable report.ObservedAt |> Option.map _.UtcDateTime |> orReject (impossible "observedAt")
            let sourceRevisionAt = ofNullable report.SourceRevisionAt |> Option.map _.UtcDateTime
            do! require (sourceRevisionAt |> Option.forall (fun revision -> revision <= observedAt)) MlbScoreboardRejection.RevisionAfterObservation
            let inning = ofNullable report.Inning
            do! require (inning |> Option.forall (fun inning -> inning >= 1)) (impossible "inning")
            do! require (Option.map2 (fun (a: _ list) (h: _ list) -> h.Length <= a.Length && a.Length <= h.Length + 1) away.Innings home.Innings
                         |> Option.defaultValue true) (impossible "innings")
            do! require (Option.map2 (fun (a: _ list) inning -> a.Length <= inning) away.Innings inning |> Option.defaultValue true) (impossible "inning")
            let! half = half inning report.Half
            let! count = count report.Count
            let! bases = bases report.Bases
            let matchup = ofObj report.Matchup |> Option.map (fun m -> { Batter = text m.Batter; Pitcher = text m.Pitcher })
            return
                { Report = report; ObservedAt = observedAt; SourceRevisionAt = sourceRevisionAt; Provider = provider
                  Provenance = provenance; State = state; Detail = detail; Inning = inning; Half = half; Count = count
                  Bases = bases; Matchup = matchup; AwayLine = away; HomeLine = home }
        }

    let private stored (game: Game) =
        match text game.MlbScoreboardJson with
        | None -> None
        | Some json ->
            try
                JsonSerializer.Deserialize<MlbScoreboardReport>(json, JsonSerializerOptions.Web)
                |> ofObj |> Option.bind (validate game >> Result.toOption)
            with :? JsonException -> None

    let accept (now: DateTime) (game: Game) (report: MlbScoreboardReport) : Result<MlbScoreboardFrame, MlbScoreboardRejection> =
        result {
            let! frame = validate game report
            do! require (frame.ObservedAt <= now) MlbScoreboardRejection.FutureObservation
            let previous = stored game
            do! require (previous |> Option.forall (fun p -> frame.ObservedAt > p.ObservedAt)) MlbScoreboardRejection.RegressingObservation
            do! require (previous |> Option.forall (fun p -> frame.Provider = p.Provider)) MlbScoreboardRejection.ProviderChanged
            let highWater = ofNullable game.ScoreboardSourceRevisionAt
            do! require (Option.map2 (>=) frame.SourceRevisionAt highWater |> Option.defaultValue true) MlbScoreboardRejection.RegressingRevision
            return frame
        }

    let internal write (game: Game) (frame: MlbScoreboardFrame) =
        game.MlbScoreboardJson <- JsonSerializer.Serialize(frame.Report, JsonSerializerOptions.Web)
        game.ScoreboardSourceRevisionAt <-
            match [ frame.SourceRevisionAt; ofNullable game.ScoreboardSourceRevisionAt ] |> List.choose id with
            | [] -> Nullable()
            | revisions -> Nullable(List.max revisions)
        game.AwayScore <- Nullable frame.AwayLine.Runs
        game.HomeScore <- Nullable frame.HomeLine.Runs
        game.StatusDetail <- frame.Detail
        game.Period <- toNullable frame.Inning
        game.ScoreboardUpdatedAt <- Nullable frame.ObservedAt

    let private cells (line: MlbTeamLine) =
        line.Innings |> Option.defaultValue [] |> List.map (Option.map MlbLineCell.Runs >> Option.defaultValue MlbLineCell.Missing)

    let private length (line: MlbTeamLine) = line.Innings |> Option.map List.length |> Option.defaultValue 0

    let private board (frame: MlbScoreboardFrame) =
        let away, home = frame.AwayLine, frame.HomeLine
        let unplayedHomeHalf =
            frame.State = MlbGameState.Final && home.Innings.IsSome && length home + 1 = length away && home.Runs > away.Runs
        let score (line: MlbTeamLine) cells = { Runs = line.Runs; Hits = line.Hits; Errors = line.Errors; Line = cells }
        { Away = score away (cells away)
          Home = score home (cells home @ (if unplayedHomeHalf then [ MlbLineCell.NotPlayed ] else []))
          Innings = max (length away) (length home)
          Detail = frame.Detail
          Inning = frame.Inning
          ObservedAt = frame.ObservedAt }

    let private phase (now: DateTime) (game: Game) (frame: MlbScoreboardFrame) =
        let age = now - frame.ObservedAt
        match frame.State with
        | MlbGameState.Scheduled -> MlbScoreboardPhase.Pregame
        | MlbGameState.InProgress when frame.Provenance = MlbProvenance.Recorded || game.IsHistoricalImport -> MlbScoreboardPhase.Recorded
        | MlbGameState.InProgress when age >= TimeSpan.Zero && age < ActiveContextMaxAge -> MlbScoreboardPhase.Live
        | MlbGameState.InProgress -> MlbScoreboardPhase.Stale
        | MlbGameState.Delayed -> MlbScoreboardPhase.Delayed
        | MlbGameState.Suspended -> MlbScoreboardPhase.Suspended
        | MlbGameState.Postponed -> MlbScoreboardPhase.Postponed
        | MlbGameState.Canceled -> MlbScoreboardPhase.Canceled
        | MlbGameState.Final -> MlbScoreboardPhase.Final

    let resolve (now: DateTime) (game: Game) : MlbScoreboardView =
        let completed = MlbCompletionEvidence.IsActuallyCompleted game
        match stored game with
        | None -> { Phase = MlbScoreboardPhase.Unavailable; Board = None; Active = None; Completed = completed }
        | Some frame ->
            let phase = phase now game frame
            { Phase = phase
              Board = Some(board frame)
              Active =
                if phase = MlbScoreboardPhase.Live then
                    Some { Half = frame.Half; Count = frame.Count; Bases = frame.Bases; Matchup = frame.Matchup }
                else None
              Completed = completed }

[<AbstractClass; Sealed>]
type MlbScoreboardStore private () =
    static member AcceptAsync(db: ExcitementDbContext, timeProvider: TimeProvider, report: MlbScoreboardReport, cancellationToken: CancellationToken)
        : Task<Result<MlbScoreboardFrame, MlbScoreboardRejection>> =
        task {
            let gameId = report |> ofObj |> Option.map _.GameId |> Option.defaultValue null
            use! transaction = db.Database.BeginTransactionAsync(cancellationToken)
            let! game =
                if db.Database.IsNpgsql() then
                    task {
                        do! db.LockProHypeFenceAsync(ProHypeFenceAccess.WriteGames, cancellationToken)
                        let admission: FormattableString = $"SELECT pg_advisory_xact_lock(683, hashtext({gameId}))"
                        let! _ = db.Database.ExecuteSqlInterpolatedAsync(admission, cancellationToken)
                        let row: FormattableString = $"SELECT * FROM games WHERE id = {gameId} FOR UPDATE"
                        return! db.Games.FromSqlInterpolated(row).SingleOrDefaultAsync(cancellationToken)
                    }
                else db.Games.SingleOrDefaultAsync((fun g -> g.Id = gameId), cancellationToken)
            match ofObj game with
            | None -> return Error MlbScoreboardRejection.WrongGame
            | Some game ->
                let accepted = MlbScoreboard.accept (timeProvider.GetUtcNow().UtcDateTime) game report
                match accepted with
                | Ok frame ->
                    MlbScoreboard.write game frame
                    let! _ = db.SaveChangesAsync(cancellationToken)
                    do! transaction.CommitAsync(cancellationToken)
                | Error _ -> ()
                return accepted
        }
