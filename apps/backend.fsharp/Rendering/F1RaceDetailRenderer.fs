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
        Elem.div [ cls "f1-driver-card"; Attr.style $"border-left: 3px solid {teamColor}" ] [
            match driver.HeadshotUrl with
            | null -> Elem.div [ cls "f1-driver-number" ] [
                        Text.raw $"{driver.DriverNumber}"
                       ]
            | url -> Elem.img [ Attr.src url; Attr.alt (if isNull driver.FullName then "" else driver.FullName); cls "f1-driver-headshot" ]
            Elem.div [ cls "f1-driver-info" ] [
                Elem.div [ cls "driver-card-header" ] [
                    Elem.span [ cls "f1-driver-car" ] [ Text.raw $"#{driver.DriverNumber}" ]
                    if not (isNull driver.Abbreviation) then
                        Elem.span [ cls "f1-driver-abbr" ] [ Text.enc driver.Abbreviation ]
                ]
                Elem.div [ cls "f1-driver-name" ] [ Text.enc (if isNull driver.FullName then "" else driver.FullName) ]
                if not (isNull driver.TeamName) then
                    Elem.div [ cls "f1-driver-team" ] [ Text.enc driver.TeamName ]
            ]
        ]

    let Render (race: F1Race) (drivers: Collections.Generic.List<F1Driver>) (leaderboard: Collections.Generic.List<F1LeaderboardEntry>) (hasSnapshots: bool) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"
        [
            Elem.div [ cls "page-narrow"; Attr.create "data-live-terminal" (if isFinal then "true" else "false") ] [
                Elem.div [ cls "detail-topbar" ] [
                    Elem.a [ Attr.href $"/f1?year={race.Year}"; cls "back-link" ] [
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
                        Text.enc (if isNull race.MeetingName then "" else race.MeetingName)
                    ]
                    Elem.div [ cls "next-race-meta" ] [
                        if not (isNull race.CircuitName) then
                            Text.enc race.CircuitName
                            if not (isNull race.Country) then Text.raw " · "
                        if not (isNull race.Country) then Text.enc race.Country
                    ]
                    Elem.div [ cls "next-race-meta" ] [
                        Text.raw $"Round {race.RoundNumber}"
                    ]
                    if isLive && race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                        Elem.div [ cls "race-laps" ] [
                            Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                        ]
                    else
                        Elem.div [ cls "next-race-meta" ] [
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
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [
                            Text.raw (if isLive then "Live Leaderboard" elif isFinal then "Final Standings" else "Leaderboard")
                        ]
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
                            Attr.create "sport" "f1"
                            Attr.create "data-live-final" (if isFinal then "true" else "false")
                            cls "odds-chart"
                        ] []
                    ]
                if leaderboard.Count = 0 && drivers.Count > 0 then
                    Elem.div [ cls "panel" ] [
                        Elem.h2 [ cls "panel-title" ] [ Text.raw "Driver Grid" ]
                        Elem.div [ cls "driver-grid" ] [
                            for driver in drivers do
                                driverCard driver
                        ]
                    ]
            ]
        ]
