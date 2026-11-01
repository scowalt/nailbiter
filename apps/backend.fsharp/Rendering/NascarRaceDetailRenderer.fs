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
                | 1 -> "GREEN", "bg-green-500/20 text-green-400"
                | 2 -> "CAUTION", "bg-yellow-500/20 text-yellow-400"
                | 3 -> "RED", "bg-red-500/20 text-red-400"
                | 4 -> "CHECKERED", "bg-white/20 text-white"
                | 5 -> "WHITE", "bg-white/20 text-gray-300"
                | _ -> "", ""
            if label <> "" then
                [ Elem.span [ cls $"text-xs font-medium px-1.5 py-0.5 rounded {flagClass}" ] [
                    Text.raw label
                ] ]
            else []

    let private manufacturerBadgeNode (manufacturer: string) : XmlNode list =
        if isNull manufacturer || manufacturer = "" then []
        else
            let mfgClass =
                match manufacturer.ToLower() with
                | "chevrolet" -> "border-yellow-500 text-yellow-400"
                | "ford" -> "border-blue-500 text-blue-400"
                | "toyota" -> "border-red-500 text-red-400"
                | _ -> "border-border text-text-muted"
            [ Elem.span [ cls $"text-xs px-1 py-0.5 rounded border {mfgClass}" ] [
                Text.enc manufacturer
            ] ]

    let private leaderboardRow (entry: NascarLeaderboardEntry) =
        let pos = entry.Position |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "flex items-center gap-3 py-2 px-3 rounded-lg hover:bg-bg-card-hover transition-colors" ] [
            Elem.span [ cls "text-text-muted text-sm font-mono w-8 text-right" ] [ Text.raw $"P{pos}" ]
            if not (isNull entry.CarNumber) then
                Elem.span [ cls "text-xs font-mono px-1.5 py-0.5 rounded bg-bg-card-hover text-text-muted" ] [
                    Text.raw $"#{entry.CarNumber}"
                ]
            if not (isNull entry.CountryFlagHref) then
                Elem.img [ Attr.src entry.CountryFlagHref; Attr.alt ""; cls "w-5 h-3 object-cover" ]
            Elem.div [ cls "flex-1 min-w-0" ] [
                Elem.span [ cls "font-medium text-text-primary text-sm truncate" ] [
                    Text.enc entry.DisplayName
                ]
            ]
            yield! manufacturerBadgeNode (if isNull entry.Manufacturer then "" else entry.Manufacturer)
            if entry.IsOnTrack.HasValue && not entry.IsOnTrack.Value then
                Elem.span [ cls "text-xs px-1 py-0.5 rounded bg-yellow-500/20 text-yellow-400" ] [
                    Text.raw "PIT"
                ]
            if entry.WinProbability.HasValue then
                Elem.div [ cls "flex items-center gap-2 w-24" ] [
                    Elem.div [ cls "flex-1 h-1.5 rounded-full overflow-hidden bg-bg-card-hover" ] [
                        Elem.div [ cls "h-full rounded-full bg-accent"; Attr.style $"width: {entry.WinProbability.Value * 100.0:F1}%%" ] []
                    ]
                    Elem.span [ cls "text-text-muted text-xs tabular-nums w-10 text-right" ] [
                        Text.raw $"{entry.WinProbability.Value * 100.0:F1}%%"
                    ]
                ]
        ]

    let private resultRow (result: NascarRaceResultWithManufacturer) =
        let pos = result.FinishPosition |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "flex items-center gap-3 py-2 px-3 rounded-lg hover:bg-bg-card-hover transition-colors" ] [
            Elem.span [ cls "text-text-muted text-sm font-mono w-8 text-right" ] [ Text.raw $"P{pos}" ]
            if not (isNull result.CarNumber) then
                Elem.span [ cls "text-xs font-mono px-1.5 py-0.5 rounded bg-bg-card-hover text-text-muted" ] [
                    Text.raw $"#{result.CarNumber}"
                ]
            if not (isNull result.CountryFlagHref) then
                Elem.img [ Attr.src result.CountryFlagHref; Attr.alt ""; cls "w-5 h-3 object-cover" ]
            Elem.div [ cls "flex-1 min-w-0" ] [
                Elem.span [ cls "font-medium text-text-primary text-sm truncate" ] [
                    Text.enc result.DisplayName
                ]
            ]
            yield! manufacturerBadgeNode (if isNull result.Manufacturer then "" else result.Manufacturer)
            if result.Winner then
                Elem.span [ cls "text-xs px-1.5 py-0.5 rounded bg-excitement-high/20 text-excitement-high font-medium" ] [
                    Text.raw "Winner"
                ]
        ]

    let private startingGridRow (result: NascarRaceResultWithManufacturer) =
        let pos = result.StartingPosition |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "flex items-center gap-3 py-2 px-3 rounded-lg hover:bg-bg-card-hover transition-colors" ] [
            Elem.span [ cls "text-text-muted text-sm font-mono w-8 text-right" ] [ Text.raw $"P{pos}" ]
            if not (isNull result.CarNumber) then
                Elem.span [ cls "text-xs font-mono px-1.5 py-0.5 rounded bg-bg-card-hover text-text-muted" ] [
                    Text.raw $"#{result.CarNumber}"
                ]
            if not (isNull result.CountryFlagHref) then
                Elem.img [ Attr.src result.CountryFlagHref; Attr.alt ""; cls "w-5 h-3 object-cover" ]
            Elem.div [ cls "flex-1 min-w-0" ] [
                Elem.span [ cls "font-medium text-text-primary text-sm truncate" ] [
                    Text.enc result.DisplayName
                ]
            ]
            yield! manufacturerBadgeNode (if isNull result.Manufacturer then "" else result.Manufacturer)
            if pos = 1 then
                Elem.span [ cls "text-xs px-1.5 py-0.5 rounded bg-accent/20 text-accent font-medium" ] [
                    Text.raw "Pole"
                ]
        ]

    let Render (race: NascarRace) (leaderboard: Collections.Generic.List<NascarLeaderboardEntry>) (results: Collections.Generic.List<NascarRaceResultWithManufacturer>) (startingGrid: Collections.Generic.List<NascarRaceResultWithManufacturer>) (drivers: Collections.Generic.List<NascarDriver>) (hasSnapshots: bool) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"
        [
            Elem.div [ cls "max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8"; Attr.create "data-live-terminal" (if isFinal then "true" else "false") ] [
                Elem.div [ cls "flex items-center justify-between mb-6" ] [
                    Elem.a [ Attr.href $"/nascar?year={race.Year}"; cls "text-text-muted hover:text-text-primary text-sm" ] [
                        Text.raw "← Back to calendar"
                    ]
                    Elem.div [ cls "flex items-center gap-2" ] [
                        if race.ExcitementScore.HasValue && (isLive || isFinal) then
                            let level = FormatHelpers.getExcitementLevel race.ExcitementScore
                            let levelClass = FormatHelpers.excitementLevelClass level
                            Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw "Excitement" ]
                            Elem.span [ cls $"text-xl font-bold tabular-nums {levelClass}" ] [
                                Text.raw $"{Math.Round(race.ExcitementScore.Value)}"
                            ]
                        yield! flagBadgeNode race.FlagState
                        if isLive then
                            Elem.span [ cls "text-xs font-medium px-2 py-1 rounded bg-status-live/20 text-status-live" ] [ Text.raw "LIVE" ]
                        elif isFinal then
                            Elem.span [ cls "text-xs font-medium px-2 py-1 rounded bg-status-final/20 text-status-final" ] [ Text.raw "Final" ]
                        else
                            Elem.span [ cls "text-xs font-medium px-2 py-1 rounded bg-status-scheduled/20 text-status-scheduled" ] [ Text.raw "Scheduled" ]
                    ]
                ]
                Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                    Elem.h1 [ cls "text-xl font-semibold text-text-primary" ] [
                        Text.enc (if isNull race.RaceName then "" else race.RaceName)
                    ]
                    Elem.div [ cls "text-text-muted text-sm mt-1" ] [ Text.raw $"Race {race.RaceNumber}" ]
                    if isLive && race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                        Elem.div [ cls "text-text-primary font-semibold mt-2" ] [
                            Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                        ]
                    else
                        Elem.div [ cls "text-text-muted text-sm mt-1" ] [
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
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Live Leaderboard" ]
                        Elem.create "nb-race-leaderboard" [
                            Attr.create "data" lbJsonEncoded
                            cls "block"
                        ] []
                    ]
                if hasSnapshots then
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Leader Win Probability" ]
                        Elem.create "nb-win-probability-chart" [
                            Attr.create "race-id" race.Id
                            Attr.create "sport" "nascar"
                            Attr.create "data-live-final" (if isFinal then "true" else "false")
                            cls "block h-64"
                        ] []
                    ]
                if isFinal && results.Count > 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Final Standings" ]
                        Elem.div [ cls "space-y-1" ] [
                            for r in results do
                                resultRow r
                        ]
                    ]
                elif isFinal && leaderboard.Count > 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Final Positions" ]
                        Elem.div [ cls "space-y-1" ] [
                            for entry in leaderboard do
                                leaderboardRow entry
                        ]
                    ]
                if startingGrid.Count > 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Starting Grid" ]
                        Elem.div [ cls "space-y-1" ] [
                            for r in startingGrid do
                                startingGridRow r
                        ]
                    ]
                if leaderboard.Count = 0 && results.Count = 0 && drivers.Count > 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Drivers" ]
                        Elem.div [ cls "grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3" ] [
                            for driver in drivers do
                                Elem.div [ cls "bg-bg-card border border-border rounded-lg p-3" ] [
                                    Elem.div [ cls "flex items-center gap-2" ] [
                                        if not (isNull driver.CarNumber) then
                                            Elem.span [ cls "text-xs font-mono px-1 py-0.5 rounded bg-bg-card-hover text-text-muted" ] [
                                                Text.raw $"#{driver.CarNumber}"
                                            ]
                                        if not (isNull driver.CountryFlagHref) then
                                            Elem.img [ Attr.src driver.CountryFlagHref; Attr.alt ""; cls "w-4 h-3 object-cover" ]
                                    ]
                                    Elem.div [ cls "font-medium text-text-primary text-sm mt-1" ] [
                                        Text.enc (if isNull driver.DisplayName then "" else driver.DisplayName)
                                    ]
                                    if not (isNull driver.TeamName) then
                                        Elem.div [ cls "text-text-muted text-xs" ] [ Text.enc driver.TeamName ]
                                ]
                        ]
                    ]
            ]
        ]
