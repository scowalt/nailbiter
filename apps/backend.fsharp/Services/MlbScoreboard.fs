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

    let validate (_: Game) (_: MlbScoreboardReport) : Result<MlbScoreboardFrame, MlbScoreboardRejection> =
        Error(MlbScoreboardRejection.Impossible "unimplemented")

    let accept (_: DateTime) (game: Game) (report: MlbScoreboardReport) = validate game report

    let resolve (_: DateTime) (_: Game) : MlbScoreboardView =
        { Phase = MlbScoreboardPhase.Unavailable; Board = None; Active = None; Completed = false }

[<AbstractClass; Sealed>]
type MlbScoreboardStore private () =
    static member AcceptAsync(_: ExcitementDbContext, timeProvider: TimeProvider, report: MlbScoreboardReport, _: CancellationToken)
        : Task<Result<MlbScoreboardFrame, MlbScoreboardRejection>> =
        Task.FromResult(MlbScoreboard.accept (timeProvider.GetUtcNow().UtcDateTime) Unchecked.defaultof<Game> report)
