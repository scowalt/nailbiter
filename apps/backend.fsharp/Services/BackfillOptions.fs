namespace backend.Services.Backfill

open System
open System.Collections.Generic

[<CLIMutable>]
type BackfillOptions =
    {  
      SkipExisting: bool
      StartWeek: Nullable<int>
      EndWeek: Nullable<int>
      SeasonType: Nullable<int>
      DryRun: bool
      CalculateElo: bool
      BackfillKalshiOdds: bool
      KalshiCandlestickInterval: int }

    static member Default =
        { SkipExisting = true
          StartWeek = Nullable()
          EndWeek = Nullable()
          SeasonType = Nullable()
          DryRun = false
          CalculateElo = false
          BackfillKalshiOdds = false
          KalshiCandlestickInterval = 60 }

[<CLIMutable>]
type BackfillResult =
    { mutable GamesCreated: int
      mutable GamesUpdated: int
      mutable GamesSkipped: int
      mutable PregameOddsCreated: int
      mutable TeamRatingsCreated: int
      mutable KalshiOddsSnapshotsCreated: int
      Errors: ResizeArray<string>
      mutable Duration: TimeSpan }

    static member Create() =
        { GamesCreated = 0
          GamesUpdated = 0
          GamesSkipped = 0
          PregameOddsCreated = 0
          TeamRatingsCreated = 0
          KalshiOddsSnapshotsCreated = 0
          Errors = ResizeArray<string>()
          Duration = TimeSpan.Zero }
