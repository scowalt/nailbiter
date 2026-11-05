namespace backend.Services.Espn

open System
open System.Text.Json
open System.Text.Json.Serialization
open Backend.FSharp

type EspnOddsValue =
    { [<JsonPropertyName("odds")>] Odds: string }

type EspnMoneylineValue =
    { [<JsonPropertyName("close")>] Close: EspnOddsValue }

type EspnMoneyline =
    { [<JsonPropertyName("home")>] Home: EspnMoneylineValue
      [<JsonPropertyName("away")>] Away: EspnMoneylineValue
      [<JsonPropertyName("draw")>] Draw: EspnMoneylineValue }

type EspnDrawOdds =
    { [<JsonPropertyName("moneyLine")>] MoneyLine: Nullable<int> }

type EspnOddsProvider =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("name")>] Name: string }

type EspnOdds =
    { [<JsonPropertyName("provider")>] Provider: EspnOddsProvider
      [<JsonPropertyName("spread")>] Spread: Nullable<float>
      [<JsonPropertyName("overUnder")>] OverUnder: Nullable<float>
      [<JsonPropertyName("drawOdds")>] DrawOdds: EspnDrawOdds
      [<JsonPropertyName("moneyline")>] Moneyline: EspnMoneyline }

type EspnThreeWayOdds =
    { HomeMoneyline: int
      DrawMoneyline: int
      AwayMoneyline: int
      Spread: Nullable<float>
      OverUnder: Nullable<float>
      ProviderName: string }

module EspnOddsParsing =
    let tryParseMoneyline (odds: string) =
        backend.Services.OddsConverter.TryParseMoneyline(odds)

    let private tryCloseMoneyline (value: EspnMoneylineValue) =
        if isNull (box value) || isNull (box value.Close) || isNull value.Close.Odds then None
        else tryParseMoneyline value.Close.Odds

    let private tryDrawMoneyline (odds: EspnOdds) =
        let fromMoneyline =
            if isNull (box odds.Moneyline) then None
            else tryCloseMoneyline odds.Moneyline.Draw

        match fromMoneyline with
        | Some value -> Some value
        | None ->
            if not (isNull (box odds.DrawOdds)) && odds.DrawOdds.MoneyLine.HasValue then
                Some odds.DrawOdds.MoneyLine.Value
            else None

    let tryGetThreeWayOdds (odds: EspnOdds) =
        if isNull (box odds) || isNull (box odds.Moneyline) then None
        else
            match tryCloseMoneyline odds.Moneyline.Home, tryDrawMoneyline odds, tryCloseMoneyline odds.Moneyline.Away with
            | Some home, Some draw, Some away ->
                Some {
                    HomeMoneyline = home
                    DrawMoneyline = draw
                    AwayMoneyline = away
                    Spread = odds.Spread
                    OverUnder = odds.OverUnder
                    ProviderName = if not (isNull (box odds.Provider)) then odds.Provider.Name else "espn"
                }
            | _ -> None

    let tryFindCompleteThreeWayOdds (odds: ResizeArray<EspnOdds>) =
        odds
        |> seqOfObj
        |> Seq.tryPick tryGetThreeWayOdds

type EspnTeam =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("displayName")>] DisplayName: string
      [<JsonPropertyName("abbreviation")>] Abbreviation: string }

type EspnCompetitor =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("homeAway")>] HomeAway: string
      [<JsonPropertyName("score")>] Score: string
      [<JsonPropertyName("team")>] Team: EspnTeam }

type EspnCompetitionType =
    { [<JsonPropertyName("abbreviation")>] Abbreviation: string }

type EspnNote =
    { [<JsonPropertyName("headline")>] Headline: string }

type EspnStatusType =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("state")>] State: string
      [<JsonPropertyName("completed")>] Completed: Nullable<bool>
      [<JsonPropertyName("description")>] Description: string }

type EspnStatus =
    { [<JsonPropertyName("clock")>] Clock: float
      [<JsonPropertyName("displayClock")>] DisplayClock: string
      [<JsonPropertyName("period")>] Period: int
      [<JsonPropertyName("type")>] Type: EspnStatusType
      [<JsonPropertyName("detail")>] Detail: string
      [<JsonPropertyName("shortDetail")>] ShortDetail: string }

type EspnCompetition =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("competitors")>] Competitors: ResizeArray<EspnCompetitor>
      [<JsonPropertyName("odds")>] Odds: ResizeArray<EspnOdds>
      [<JsonPropertyName("type")>] Type: EspnCompetitionType
      [<JsonPropertyName("notes")>] Notes: ResizeArray<EspnNote>
      [<JsonPropertyName("series")>] Series: JsonElement
      [<JsonPropertyName("broadcasts")>] Broadcasts: JsonElement
      [<JsonPropertyName("geoBroadcasts")>] GeoBroadcasts: JsonElement
      [<JsonPropertyName("status")>] Status: EspnStatus }

module EspnBroadcasts =
    let maxNetworks = 8
    let private maxNameLength = 40

    let private property (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private text (element: JsonElement option) =
        match element with
        | Some value when value.ValueKind = JsonValueKind.String -> Some (value.GetString())
        | _ -> None

    let private usableName (name: string) =
        let trimmed = if isNull name then "" else name.Trim()
        if trimmed.Length = 0 || trimmed.Length > maxNameLength || trimmed |> Seq.exists Char.IsControl then None
        else Some trimmed

    let private marketRank (market: string option) =
        match market |> Option.map (fun m -> m.Trim().ToLowerInvariant()) with
        | Some "national" -> 0
        | Some "home" -> 1
        | Some "away" -> 2
        | _ -> 3

    let private items (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Array then element.EnumerateArray() |> Seq.toList else []

    let private fromBroadcasts (broadcasts: JsonElement) =
        items broadcasts
        |> List.collect (fun entry ->
            let rank = marketRank (text (property entry "market"))
            match property entry "names" with
            | Some names when names.ValueKind = JsonValueKind.Array ->
                names.EnumerateArray()
                |> Seq.choose (fun name -> if name.ValueKind = JsonValueKind.String then usableName (name.GetString()) else None)
                |> Seq.map (fun name -> rank, name)
                |> Seq.toList
            | _ -> [])

    let private fromGeoBroadcasts (geoBroadcasts: JsonElement) =
        items geoBroadcasts
        |> List.choose (fun entry ->
            let kind = property entry "type" |> Option.bind (fun t -> text (property t "shortName"))
            let isRadio = kind |> Option.exists (fun k -> k.Trim().Equals("Radio", StringComparison.OrdinalIgnoreCase))
            let market = property entry "market" |> Option.bind (fun m -> text (property m "type"))
            let name = property entry "media" |> Option.bind (fun m -> text (property m "shortName")) |> Option.bind usableName
            match name with
            | Some name when not isRadio -> Some (marketRank market, name)
            | _ -> None)

    let networks (broadcasts: JsonElement) (geoBroadcasts: JsonElement) : string list =
        let primary = fromBroadcasts broadcasts
        let candidates = if primary.IsEmpty then fromGeoBroadcasts geoBroadcasts else primary
        candidates
        |> List.indexed
        |> List.sortBy (fun (index, (rank, _)) -> rank, index)
        |> List.map (fun (_, (_, name)) -> name)
        |> List.distinctBy (fun name -> name.ToUpperInvariant())
        |> List.truncate maxNetworks

    let ofCompetition (competition: EspnCompetition) =
        if isNull (box competition) then [] else networks competition.Broadcasts competition.GeoBroadcasts

type EspnWeek =
    { [<JsonPropertyName("number")>] Number: int }

type EspnEventSeason =
    { [<JsonPropertyName("year")>] Year: int
      [<JsonPropertyName("type")>] Type: int
      [<JsonPropertyName("slug")>] Slug: string }

type EspnEvent =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("shortName")>] ShortName: string
      [<JsonPropertyName("date")>] Date: DateTime
      [<JsonPropertyName("status")>] Status: EspnStatus
      [<JsonPropertyName("competitions")>] Competitions: ResizeArray<EspnCompetition>
      [<JsonPropertyName("week")>] Week: EspnWeek
      [<JsonPropertyName("season")>] Season: EspnEventSeason }

type EspnSeasonType =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("type")>] Type: int
      [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("abbreviation")>] Abbreviation: string }

type EspnLeagueSeason =
    { [<JsonPropertyName("year")>] Year: int
      [<JsonPropertyName("type")>] Type: EspnSeasonType }

type EspnLeague =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("slug")>] Slug: string
      [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("abbreviation")>] Abbreviation: string
      [<JsonPropertyName("season")>] Season: EspnLeagueSeason
      [<JsonPropertyName("calendar")>] Calendar: JsonElement }

type EspnSeason =
    { [<JsonPropertyName("type")>] Type: int
      [<JsonPropertyName("year")>] Year: int }

type EspnScoreboardResponse =
    { [<JsonPropertyName("events")>] Events: ResizeArray<EspnEvent>
      [<JsonPropertyName("week")>] Week: EspnWeek
      [<JsonPropertyName("season")>] Season: EspnSeason
      [<JsonPropertyName("leagues")>] Leagues: ResizeArray<EspnLeague>
      [<JsonIgnore>] RawJson: JsonElement }

module EspnSeasonMetadata =
    let seasonType (eventSeason: EspnEventSeason) (scoreboard: EspnScoreboardResponse) =
        let providerType =
            if not (isNull (box eventSeason)) && eventSeason.Type > 0 then
                Nullable<int>(eventSeason.Type)
            elif not (isNull (box scoreboard.Season)) && scoreboard.Season.Type > 0 then
                Nullable<int>(scoreboard.Season.Type)
            else
                Nullable<int>()
        let seasonSlug = if isNull (box eventSeason) then null else eventSeason.Slug
        let leagueSlug =
            if not (isNull scoreboard.Leagues) && scoreboard.Leagues.Count = 1 && not (isNull (box scoreboard.Leagues[0])) then scoreboard.Leagues[0].Slug
            else null
        backend.Services.GameVisibilityPolicy.ClassifySeasonType(providerType, seasonSlug, leagueSlug)

type EspnCoreMoneyLineValue =
    { [<JsonPropertyName("american")>] American: string }

type EspnCoreCloseOdds =
    { [<JsonPropertyName("moneyLine")>] MoneyLine: EspnCoreMoneyLineValue }

type EspnCoreTeamOdds =
    { [<JsonPropertyName("moneyLine")>] MoneyLine: Nullable<int>
      [<JsonPropertyName("close")>] Close: EspnCoreCloseOdds }

type EspnCoreOddsItem =
    { [<JsonPropertyName("spread")>] Spread: Nullable<float>
      [<JsonPropertyName("overUnder")>] OverUnder: Nullable<float>
      [<JsonPropertyName("homeTeamOdds")>] HomeTeamOdds: EspnCoreTeamOdds
      [<JsonPropertyName("awayTeamOdds")>] AwayTeamOdds: EspnCoreTeamOdds }

type EspnCoreOddsResponse =
    { [<JsonPropertyName("items")>] Items: ResizeArray<EspnCoreOddsItem> }
