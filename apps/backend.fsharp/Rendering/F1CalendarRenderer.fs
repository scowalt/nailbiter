namespace Backend.Rendering

open System
open Falco.Markup
open backend.Models

module F1CalendarRenderer =

    let private cls = Layout.cls

    let private getCountryFlag (country: string) =
        match country with
        | "Bahrain" -> "\U0001F1E7\U0001F1ED"
        | "Saudi Arabia" -> "\U0001F1F8\U0001F1E6"
        | "Australia" -> "\U0001F1E6\U0001F1FA"
        | "Japan" -> "\U0001F1EF\U0001F1F5"
        | "China" -> "\U0001F1E8\U0001F1F3"
        | "USA" | "United States" -> "\U0001F1FA\U0001F1F8"
        | "Monaco" -> "\U0001F1F2\U0001F1E8"
        | "Canada" -> "\U0001F1E8\U0001F1E6"
        | "Spain" -> "\U0001F1EA\U0001F1F8"
        | "Austria" -> "\U0001F1E6\U0001F1F9"
        | "UK" | "United Kingdom" | "Great Britain" -> "\U0001F1EC\U0001F1E7"
        | "Hungary" -> "\U0001F1ED\U0001F1FA"
        | "Belgium" -> "\U0001F1E7\U0001F1EA"
        | "Netherlands" | "The Netherlands" -> "\U0001F1F3\U0001F1F1"
        | "Italy" -> "\U0001F1EE\U0001F1F9"
        | "Azerbaijan" -> "\U0001F1E6\U0001F1FF"
        | "Singapore" -> "\U0001F1F8\U0001F1EC"
        | "Mexico" -> "\U0001F1F2\U0001F1FD"
        | "Brazil" -> "\U0001F1E7\U0001F1F7"
        | "Qatar" -> "\U0001F1F6\U0001F1E6"
        | "Abu Dhabi" | "UAE" -> "\U0001F1E6\U0001F1EA"
        | _ -> "\U0001F3C1"

    let private raceRow (race: F1Race) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"
        let flag = getCountryFlag (if isNull race.Country then "" else race.Country)

        Elem.a [ Attr.href $"/f1/race/{race.Id}"; cls "race-card" ] [
            Elem.div [ cls "race-card-body" ] [
                Elem.div [ cls "race-card-main" ] [
                    Elem.span [ cls "race-number" ] [
                        Text.raw $"R{race.RoundNumber}"
                    ]
                    Elem.span [ cls "race-flag" ] [ Text.raw flag ]
                    Elem.div [ cls "race-card-text" ] [
                        Elem.span [ cls "race-name" ] [
                            Text.enc (if isNull race.MeetingName then "" else race.MeetingName)
                        ]
                    ]
                ]
                Elem.div [ cls "race-card-aside" ] [
                    if isLive then
                        Elem.span [ cls "race-badge-live" ] [
                            Text.raw "LIVE"
                        ]
                        if race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                            Elem.span [ cls "meta-text" ] [
                                Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                            ]
                    elif isFinal then
                        Elem.span [ cls "race-badge-final" ] [
                            Text.raw "Final"
                        ]
                    else
                        Elem.span [ cls "meta-text" ] [
                            FormatHelpers.scheduledTimeNode race.ScheduledStart
                        ]
                    if race.ExcitementScore.HasValue && (isLive || isFinal) then
                        let level = FormatHelpers.getExcitementLevel race.ExcitementScore
                        let levelClass = FormatHelpers.excitementLevelClass level
                        Elem.span [ cls $"race-score {levelClass}" ] [
                            Text.raw $"{Math.Round(race.ExcitementScore.Value)}"
                        ]
                    Elem.span [ cls "race-card-arrow" ] [
                        Text.raw "→"
                    ]
                ]
            ]
        ]

    let Render (year: int) (races: Collections.Generic.List<F1Race>) =
        let nextRace =
            races
            |> Seq.tryFind (fun r -> r.Status = "scheduled" && r.ScheduledStart > DateTime.UtcNow)

        [
            Elem.div [ cls "page" ] [
                Elem.div [ cls "page-header" ] [
                    Elem.h1 [ cls "league-title" ] [
                        Elem.img [ Attr.src "/logos/f1.png"; Attr.alt "F1"; cls "league-title-logo" ]
                        Elem.span [] [ Text.raw "Formula 1" ]
                    ]
                    Elem.div [ cls "week-nav" ] [
                        if year > 2023 then
                            Elem.a [ Attr.href $"/f1?year={year - 1}"; cls "week-nav-link" ] [
                                Text.raw $"← {year - 1}"
                            ]
                        Elem.span [ cls "meta-text" ] [ Text.raw $"{year} Season Calendar" ]
                        if year < DateTime.UtcNow.Year + 1 then
                            Elem.a [ Attr.href $"/f1?year={year + 1}"; cls "week-nav-link" ] [
                                Text.raw $"{year + 1} →"
                            ]
                    ]
                ]
                match nextRace with
                | Some nr ->
                    Elem.div [ cls "next-race next-race-f1" ] [
                        Elem.div [ cls "next-race-label" ] [ Text.raw "Next Race" ]
                        Elem.div [ cls "next-race-name" ] [
                            Text.raw $"Round {nr.RoundNumber}: "
                            Text.enc (if isNull nr.MeetingName then "" else nr.MeetingName)
                        ]
                        Elem.div [ cls "next-race-meta" ] [
                            if not (isNull nr.CircuitName) then
                                Text.enc nr.CircuitName
                                if not (isNull nr.Country) then
                                    Text.raw " · "
                            if not (isNull nr.Country) then
                                Text.enc nr.Country
                        ]
                        Elem.div [ cls "next-race-meta" ] [
                            FormatHelpers.scheduledTimeNode nr.ScheduledStart
                        ]
                    ]
                | None -> ()
                if races.Count = 0 then
                    Elem.div [ cls "empty-state" ] [
                        Elem.p [ cls "empty-state-text" ] [ Text.raw $"No F1 races found for {year}." ]
                    ]
                else
                    Elem.div [ cls "race-list" ] [
                        for race in races do
                            raceRow race
                    ]
            ]
        ]
