namespace Backend.Rendering

open System
open System.Text.Json
open Falco.Markup
open backend.Models
open backend.Services

module NascarRaceDetailRenderer =

    let private cls = Layout.cls

    let private flagBadgeNode (flagState: Nullable<int>) : XmlNode list =
        if not flagState.HasValue then []
        else
            let (label, flagClass) =
                match flagState.Value with
                | 1 -> "GREEN", "flag-green"
                | 2 -> "CAUTION", "flag-caution"
                | 3 -> "RED", "flag-red"
                | 4 -> "CHECKERED", "flag-checkered"
                | 5 -> "WHITE", "flag-white"
                | _ -> "", ""
            if label <> "" then
                [ Elem.span [ cls $"flag-badge {flagClass}" ] [
                    Text.raw label
                ] ]
            else []

    let private manufacturerBadgeNode (manufacturer: string) : XmlNode list =
        if isNull manufacturer || manufacturer = "" then []
        else
            let mfgClass =
                match manufacturer.ToLower() with
                | "chevrolet" -> "mfg-chevrolet"
                | "ford" -> "mfg-ford"
                | "toyota" -> "mfg-toyota"
                | _ -> "mfg-other"
            [ Elem.span [ cls $"mfg-badge {mfgClass}" ] [
                Text.enc manufacturer
            ] ]

    let private leaderboardRow (entry: NascarLeaderboardEntry) =
        let pos = entry.Position |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "standings-row" ] [
            Elem.span [ cls "standings-position" ] [ Text.raw $"P{pos}" ]
            if not (isNull entry.CarNumber) then
                Elem.span [ cls "car-number" ] [
                    Text.raw $"#{entry.CarNumber}"
                ]
            if not (isNull entry.CountryFlagHref) then
                Elem.img [ Attr.src entry.CountryFlagHref; Attr.alt ""; cls "standings-flag" ]
            Elem.div [ cls "standings-driver" ] [
                Elem.span [ cls "standings-name" ] [
                    Text.enc entry.DisplayName
                ]
            ]
            yield! manufacturerBadgeNode (if isNull entry.Manufacturer then "" else entry.Manufacturer)
            if entry.IsOnTrack.HasValue && not entry.IsOnTrack.Value then
                Elem.span [ cls "pit-badge" ] [
                    Text.raw "PIT"
                ]
            if entry.WinProbability.HasValue then
                Elem.div [ cls "standings-probability" ] [
                    Elem.div [ cls "standings-meter" ] [
                        Elem.div [ cls "standings-meter-fill"; Attr.style $"width: {entry.WinProbability.Value * 100.0:F1}%%" ] []
                    ]
                    Elem.span [ cls "standings-percent" ] [
                        Text.raw $"{entry.WinProbability.Value * 100.0:F1}%%"
                    ]
                ]
        ]

    let private resultRow (result: NascarRaceResultWithManufacturer) =
        let pos = result.FinishPosition |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "standings-row" ] [
            Elem.span [ cls "standings-position" ] [ Text.raw $"P{pos}" ]
            if not (isNull result.CarNumber) then
                Elem.span [ cls "car-number" ] [
                    Text.raw $"#{result.CarNumber}"
                ]
            if not (isNull result.CountryFlagHref) then
                Elem.img [ Attr.src result.CountryFlagHref; Attr.alt ""; cls "standings-flag" ]
            Elem.div [ cls "standings-driver" ] [
                Elem.span [ cls "standings-name" ] [
                    Text.enc result.DisplayName
                ]
            ]
            yield! manufacturerBadgeNode (if isNull result.Manufacturer then "" else result.Manufacturer)
            if result.Winner then
                Elem.span [ cls "winner-badge" ] [
                    Text.raw "Winner"
                ]
        ]

    let private startingGridRow (result: NascarRaceResultWithManufacturer) =
        let pos = result.StartingPosition |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "standings-row" ] [
            Elem.span [ cls "standings-position" ] [ Text.raw $"P{pos}" ]
            if not (isNull result.CarNumber) then
                Elem.span [ cls "car-number" ] [
                    Text.raw $"#{result.CarNumber}"
                ]
            if not (isNull result.CountryFlagHref) then
                Elem.img [ Attr.src result.CountryFlagHref; Attr.alt ""; cls "standings-flag" ]
            Elem.div [ cls "standings-driver" ] [
                Elem.span [ cls "standings-name" ] [
                    Text.enc result.DisplayName
                ]
            ]
            yield! manufacturerBadgeNode (if isNull result.Manufacturer then "" else result.Manufacturer)
            if pos = 1 then
                Elem.span [ cls "pole-badge" ] [
                    Text.raw "Pole"
                ]
        ]

    let Render (race: NascarRace) (leaderboard: Collections.Generic.List<NascarLeaderboardEntry>) (results: Collections.Generic.List<NascarRaceResultWithManufacturer>) (startingGrid: Collections.Generic.List<NascarRaceResultWithManufacturer>) (drivers: Collections.Generic.List<NascarDriver>) (hasSnapshots: bool) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"
        [
            Elem.div [ cls "page-narrow"; Attr.create "data-live-terminal" (if isFinal then "true" else "false") ] [
                Elem.div [ cls "detail-topbar" ] [
                    Elem.a [ Attr.href $"/nascar?year={race.Year}"; cls "back-link" ] [
                        Text.raw "← Back to calendar"
                    ]
                    Elem.div [ cls "race-header-badges" ] [
                        if race.ExcitementScore.HasValue && (isLive || isFinal) then
                            let level = FormatHelpers.getExcitementLevel race.ExcitementScore
                            let levelClass = FormatHelpers.excitementLevelClass level
                            Elem.span [ cls "meta-text" ] [ Text.raw "Excitement" ]
                            Elem.span [ cls $"detail-excitement-value {levelClass}" ] [
                                Text.raw $"{Math.Round(race.ExcitementScore.Value)}"
                            ]
                        yield! flagBadgeNode race.FlagState
                        if isLive then
                            Elem.span [ cls "race-status-live" ] [ Text.raw "LIVE" ]
                        elif isFinal then
                            Elem.span [ cls "race-status-final" ] [ Text.raw "Final" ]
                        else
                            Elem.span [ cls "race-status-scheduled" ] [ Text.raw "Scheduled" ]
                    ]
                ]
                Elem.div [ cls "panel" ] [
                    Elem.h1 [ cls "race-title" ] [
                        Text.enc (if isNull race.RaceName then "" else race.RaceName)
                    ]
                    Elem.div [ cls "next-race-meta" ] [ Text.raw $"Race {race.RaceNumber}" ]
                    if isLive && race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                        Elem.div [ cls "race-laps" ] [
                            Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                        ]
                    else
                        Elem.div [ cls "next-race-meta" ] [
                            FormatHelpers.scheduledTimeNode race.ScheduledStart
                        ]
                ]
                if isLive && leaderboard.Count > 0 then
                    let leaderboardJson =
                        leaderboard
                        |> Seq.map (fun e ->
                            {| position = e.Position |> Option.ofNullable |> Option.defaultValue 0
                               espnAthleteId = e.EspnAthleteId
                               fullName = e.DisplayName
                               abbreviation = e.ShortName
                               displayName = e.ShortName
                               teamColor = "#6b7280"
                               winProbability = e.WinProbability
                               headshotUrl = (null: string)
                               countryFlagHref = e.CountryFlagHref |})
                        |> Seq.toArray
                    let lbJson = JsonSerializer.Serialize(leaderboardJson, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
                    let lbJsonEncoded = System.Net.WebUtility.HtmlEncode(lbJson)
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Live Leaderboard" ]
                        Elem.create "nb-race-leaderboard" [
                            Attr.create "data" lbJsonEncoded
                            cls "race-leaderboard"
                        ] []
                    ]
                if hasSnapshots then
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Leader Win Probability" ]
                        Elem.create "nb-win-probability-chart" [
                            Attr.create "race-id" race.Id
                            Attr.create "sport" "nascar"
                            Attr.create "data-live-final" (if isFinal then "true" else "false")
                            cls "odds-chart"
                        ] []
                    ]
                if isFinal && results.Count > 0 then
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Final Standings" ]
                        Elem.div [ cls "standings-list" ] [
                            for r in results do
                                resultRow r
                        ]
                    ]
                elif isFinal && leaderboard.Count > 0 then
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Final Positions" ]
                        Elem.div [ cls "standings-list" ] [
                            for entry in leaderboard do
                                leaderboardRow entry
                        ]
                    ]
                if startingGrid.Count > 0 then
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Starting Grid" ]
                        Elem.div [ cls "standings-list" ] [
                            for r in startingGrid do
                                startingGridRow r
                        ]
                    ]
                if leaderboard.Count = 0 && results.Count = 0 && drivers.Count > 0 then
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Drivers" ]
                        Elem.div [ cls "driver-grid" ] [
                            for driver in drivers do
                                Elem.div [ cls "driver-card" ] [
                                    Elem.div [ cls "driver-card-header" ] [
                                        if not (isNull driver.CarNumber) then
                                            Elem.span [ cls "car-number-compact" ] [
                                                Text.raw $"#{driver.CarNumber}"
                                            ]
                                        if not (isNull driver.CountryFlagHref) then
                                            Elem.img [ Attr.src driver.CountryFlagHref; Attr.alt ""; cls "driver-flag" ]
                                    ]
                                    Elem.div [ cls "driver-name" ] [
                                        Text.enc (if isNull driver.DisplayName then "" else driver.DisplayName)
                                    ]
                                    if not (isNull driver.TeamName) then
                                        Elem.div [ cls "driver-team" ] [ Text.enc driver.TeamName ]
                                ]
                        ]
                    ]
            ]
        ]
