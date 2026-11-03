namespace backend.Services

open System

module internal QualificationProvenance =
    let evidenceKind source =
        match source with
        | Some "moneypuck_qualification" -> backend.Scoring.ProQualificationEvidenceKind.MoneyPuckModel
        | Some "playoffstatus" -> backend.Scoring.ProQualificationEvidenceKind.PlayoffStatusModel
        | Some "kalshi" -> backend.Scoring.ProQualificationEvidenceKind.Kalshi
        | None -> backend.Scoring.ProQualificationEvidenceKind.Unavailable
        | _ -> backend.Scoring.ProQualificationEvidenceKind.Scalar

type HypeComponents() =
    member val StakesAvailable = true with get, set
    member val QualityAvailable = true with get, set
    member val ClosenessAvailable = true with get, set
    member this.IsComplete = this.StakesAvailable && this.QualityAvailable && this.ClosenessAvailable
    member val StakesExplanation = "" with get, set
    member val QualityExplanation = "" with get, set
    member val QualitySource = "" with get, set
    member val ClosenessExplanation = "" with get, set
    member val StakesScore = 0.0 with get, set
    member val QualityScore = 0.0 with get, set
    member val ClosenessScore = 0.0 with get, set
    member val NflPlayoffLeverage: backend.Models.NflPlayoffLeverageEvidence = Unchecked.defaultof<_> with get, set
    member val HomeTeamElo: Nullable<float> = Nullable<float>() with get, set
    member val AwayTeamElo: Nullable<float> = Nullable<float>() with get, set
    member val HomePlayoffOdds: Nullable<float> = Nullable<float>() with get, set
    member val AwayPlayoffOdds: Nullable<float> = Nullable<float>() with get, set
    member val HomeQualificationSource = "" with get, set
    member val AwayQualificationSource = "" with get, set
    member val HomeQualificationEvidenceKind = backend.Scoring.ProQualificationEvidenceKind.Unavailable with get, set
    member val AwayQualificationEvidenceKind = backend.Scoring.ProQualificationEvidenceKind.Unavailable with get, set
    member val HomeQualificationIsFinal = false with get, set
    member val AwayQualificationIsFinal = false with get, set

type DerivedHypeCredit =
    { MoneyPuckTeams: string list
      QualitySource: string }

type ProCurrentHypeEvaluation(components: HypeComponents, assessment: backend.Scoring.ProHypeAssessment) =
    member _.Components = components
    member _.Assessment = assessment

type GameWithScores =
    { Game: backend.Models.Game
      ExcitementScore: Nullable<float>
      LatestOdds: backend.Models.OddsSnapshot option
      PregameOdds: backend.Models.PregameOdds option }

[<CLIMutable>]
type WorldCupCardData =
    { Game: backend.Models.Game
      HomeProbability: Nullable<float>
      DrawProbability: Nullable<float>
      AwayProbability: Nullable<float>
      HomeMoneyline: Nullable<int>
      DrawMoneyline: Nullable<int>
      AwayMoneyline: Nullable<int>
      NailbiterScore: Nullable<float>
      GameClock: string
      Period: Nullable<int>
      Source: string
      OddsTimestamp: Nullable<DateTime> }

type CurrentWeekData =
    { SeasonYear: int
      SeasonType: Nullable<int>
      WeekNumber: int
      WeekLabel: string }

type PlayoffSeriesData =
    { HigherSeedTeam: string
      LowerSeedTeam: string
      HigherSeedNumber: int
      LowerSeedNumber: int
      HigherSeedWins: int
      LowerSeedWins: int
      Completed: bool
      Winner: string option
      Conference: string }

type PlayoffRoundData =
    { Number: int
      Label: string
      Series: PlayoffSeriesData list }

type PlayoffBracketData =
    { Sport: string
      SeasonYear: int
      Rounds: PlayoffRoundData list }

type TeamRankInfo =
    { PowerRank: int option
      PlayoffOdds: float option
      PlayoffFetchedAt: DateTime option
      PlayoffIsFinal: bool
      PlayoffIsAssumed: bool
      PlayoffSource: string
      PlayoffEvidenceKind: backend.Scoring.ProQualificationEvidenceKind
      PlayoffSeed: int option
      DisplayRank: string option }

[<CLIMutable>]
type F1LeaderboardEntry =
    { EspnAthleteId: string
      Position: Nullable<int>
      WinProbability: Nullable<float>
      FullName: string
      Abbreviation: string
      TeamName: string
      TeamColor: string
      HeadshotUrl: string
      CountryCode: string }

[<CLIMutable>]
type NascarLeaderboardEntry =
    { EspnAthleteId: string
      Position: Nullable<int>
      WinProbability: Nullable<float>
      DisplayName: string
      ShortName: string
      CarNumber: string
      CountryFlagHref: string
      IsOnTrack: Nullable<bool>
      Manufacturer: string }

[<CLIMutable>]
type NascarRaceResultWithManufacturer =
    { EspnAthleteId: string
      DisplayName: string
      ShortName: string
      FinishPosition: Nullable<int>
      Winner: bool
      CountryFlagHref: string
      CarNumber: string
      StartingPosition: Nullable<int>
      Manufacturer: string }

[<CLIMutable>]
type IndyCarLeaderboardEntry =
    { EspnAthleteId: string
      Position: Nullable<int>
      WinProbability: Nullable<float>
      DisplayName: string
      ShortName: string
      CarNumber: string
      CountryFlagHref: string }
