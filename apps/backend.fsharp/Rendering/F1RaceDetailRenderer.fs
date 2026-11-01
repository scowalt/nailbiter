namespace Backend.Rendering

open System
open System.Text.Json
open Falco.Markup
open backend.Models
open backend.Services

module F1RaceDetailRenderer =

    let private cls = Layout.cls

    let private driverCard (driver: F1Driver) =
        let teamColor = if isNull driver.TeamColor then "#6b7280" else driver.TeamColor
        Elem.div [ cls "bg-bg-card border border-border rounded-lg p-3 flex items-center gap-3"; Attr.style $"border-left: 3px solid {teamColor}" ] [
            match driver.HeadshotUrl with
            | null -> Elem.div [ cls "w-10 h-10 rounded-full bg-bg-card-hover flex items-center justify-center text-text-muted text-sm font-mono" ] [
                        Text.raw $"{driver.DriverNumber}"
                       ]
            | url -> Elem.img [ Attr.src url; Attr.alt (if isNull driver.FullName then "" else driver.FullName); cls "w-10 h-10 rounded-full object-cover" ]
            Elem.div [ cls "min-w-0" ] [
                Elem.div [ cls "flex items-center gap-2" ] [
                    Elem.span [ cls "text-text-muted text-xs font-mono" ] [ Text.raw $"#{driver.DriverNumber}" ]
                    if not (isNull driver.Abbreviation) then
                        Elem.span [ cls "font-semibold text-text-primary text-sm" ] [ Text.enc driver.Abbreviation ]
                ]
                Elem.div [ cls "text-text-secondary text-sm truncate" ] [ Text.enc (if isNull driver.FullName then "" else driver.FullName) ]
                if not (isNull driver.TeamName) then
                    Elem.div [ cls "text-text-muted text-xs truncate" ] [ Text.enc driver.TeamName ]
            ]
        ]

    let private leaderboardRow (entry: F1LeaderboardEntry) =
        let teamColor = if isNull entry.TeamColor then "#6b7280" else entry.TeamColor
        let pos = entry.Position |> Option.ofNullable |> Option.defaultValue 0
        Elem.div [ cls "flex items-center gap-3 py-2 px-3 rounded-lg hover:bg-bg-card-hover transition-colors" ] [
            Elem.span [ cls "text-text-muted text-sm font-mono w-8 text-right" ] [ Text.raw $"P{pos}" ]
            Elem.div [ cls "w-1 h-5 rounded-full"; Attr.style $"background: {teamColor}" ] []
            match entry.HeadshotUrl with
            | null -> ()
            | url -> Elem.img [ Attr.src url; Attr.alt entry.FullName; cls "w-5 h-5 rounded-full object-cover" ]
            Elem.div [ cls "flex-1 min-w-0" ] [
                Elem.span [ cls "font-medium text-text-primary text-sm" ] [
                    Text.enc (if isNull entry.Abbreviation then entry.FullName else entry.Abbreviation)
                ]
                Elem.span [ cls "text-text-muted text-xs ml-2" ] [ Text.enc entry.FullName ]
            ]
            if not (isNull entry.TeamName) then
                Elem.span [ cls "text-xs px-1.5 py-0.5 rounded border border-border text-text-muted"; Attr.style $"border-color: {teamColor}" ] [
                    Text.enc entry.TeamName
                ]
            if entry.WinProbability.HasValue then
                Elem.div [ cls "flex items-center gap-2 w-24" ] [
                    Elem.div [ cls "flex-1 h-1.5 rounded-full overflow-hidden bg-bg-card-hover" ] [
                        Elem.div [ cls "h-full rounded-full"; Attr.style $"width: {entry.WinProbability.Value * 100.0:F1}%%; background: {teamColor}" ] []
                    ]
                    Elem.span [ cls "text-text-muted text-xs tabular-nums w-10 text-right" ] [
                        Text.raw $"{entry.WinProbability.Value * 100.0:F1}%%"
                    ]
                ]
        ]

    let Render (race: F1Race) (drivers: Collections.Generic.List<F1Driver>) (leaderboard: Collections.Generic.List<F1LeaderboardEntry>) (hasSnapshots: bool) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"
        [
            Elem.div [ cls "max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8"; Attr.create "data-live-terminal" (if isFinal then "true" else "false") ] [
                Elem.div [ cls "flex items-center justify-between mb-6" ] [
                    Elem.a [ Attr.href $"/f1?year={race.Year}"; cls "text-text-muted hover:text-text-primary text-sm" ] [
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
                        Text.enc (if isNull race.MeetingName then "" else race.MeetingName)
                    ]
                    Elem.div [ cls "text-text-muted text-sm mt-1" ] [
                        if not (isNull race.CircuitName) then
                            Text.enc race.CircuitName
                            if not (isNull race.Country) then Text.raw " · "
                        if not (isNull race.Country) then Text.enc race.Country
                    ]
                    Elem.div [ cls "text-text-muted text-sm mt-1" ] [
                        Text.raw $"Round {race.RoundNumber}"
                    ]
                    if isLive && race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                        Elem.div [ cls "text-text-primary font-semibold mt-2" ] [
                            Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                        ]
                    else
                        Elem.div [ cls "text-text-muted text-sm mt-1" ] [
                            FormatHelpers.scheduledTimeNode race.ScheduledStart
                        ]
                ]
                if leaderboard.Count > 0 then
                    let leaderboardJson =
                        leaderboard
                        |> Seq.map (fun e ->
                            {| position = e.Position |> Option.ofNullable |> Option.defaultValue 0
                               espnAthleteId = e.EspnAthleteId
                               fullName = e.FullName
                               abbreviation = e.Abbreviation
                               displayName = e.Abbreviation
                               teamColor = if isNull e.TeamColor then "#6b7280" else e.TeamColor
                               winProbability = e.WinProbability
                               headshotUrl = e.HeadshotUrl
                               countryFlagHref = null |})
                        |> Seq.toArray
                    let lbJson = JsonSerializer.Serialize(leaderboardJson, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
                    let lbJsonEncoded = System.Net.WebUtility.HtmlEncode(lbJson)
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [
                            Text.raw (if isLive then "Live Leaderboard" elif isFinal then "Final Standings" else "Leaderboard")
                        ]
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
                            Attr.create "sport" "f1"
                            Attr.create "data-live-final" (if isFinal then "true" else "false")
                            cls "block h-64"
                        ] []
                    ]
                if leaderboard.Count = 0 && drivers.Count > 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                        Elem.h2 [ cls "text-lg font-semibold mb-4" ] [ Text.raw "Driver Grid" ]
                        Elem.div [ cls "grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3" ] [
                            for driver in drivers do
                                driverCard driver
                        ]
                    ]
            ]
        ]
