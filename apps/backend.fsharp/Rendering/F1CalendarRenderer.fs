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
        let liveClass = if isLive then " border-[#E10600]/30 bg-[#E10600]/5" else ""

        Elem.a [ Attr.href $"/f1/race/{race.Id}"; cls $"block bg-bg-card border border-border rounded-lg p-4 hover:border-border-hover transition-colors group{liveClass}" ] [
            Elem.div [ cls "flex items-center justify-between" ] [
                Elem.div [ cls "flex items-center gap-3 min-w-0 flex-1" ] [
                    Elem.span [ cls "text-text-muted text-sm tabular-nums w-8" ] [
                        Text.raw $"R{race.RoundNumber}"
                    ]
                    Elem.span [ cls "text-lg" ] [ Text.raw flag ]
                    Elem.div [ cls "min-w-0" ] [
                        Elem.span [ cls "font-medium text-text-primary group-hover:text-accent transition-colors" ] [
                            Text.enc (if isNull race.MeetingName then "" else race.MeetingName)
                        ]
                    ]
                ]
                Elem.div [ cls "flex items-center gap-3 flex-shrink-0 ml-4" ] [
                    if isLive then
                        Elem.span [ cls "text-xs font-medium px-1.5 py-0.5 rounded bg-status-live/20 text-status-live" ] [
                            Text.raw "LIVE"
                        ]
                        if race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                            Elem.span [ cls "text-text-muted text-sm" ] [
                                Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                            ]
                    elif isFinal then
                        Elem.span [ cls "text-xs font-medium px-1.5 py-0.5 rounded bg-status-final/20 text-status-final" ] [
                            Text.raw "Final"
                        ]
                    else
                        Elem.span [ cls "text-text-muted text-sm" ] [
                            FormatHelpers.scheduledTimeNode race.ScheduledStart
                        ]
                    if race.ExcitementScore.HasValue && (isLive || isFinal) then
                        let level = FormatHelpers.getExcitementLevel race.ExcitementScore
                        let levelClass = FormatHelpers.excitementLevelClass level
                        Elem.span [ cls $"text-sm font-semibold tabular-nums {levelClass}" ] [
                            Text.raw $"{Math.Round(race.ExcitementScore.Value)}"
                        ]
                    Elem.span [ cls "text-text-muted group-hover:text-text-primary transition-colors" ] [
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
            Elem.div [ cls "max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8" ] [
                Elem.div [ cls "mb-8" ] [
                    Elem.h1 [ cls "text-2xl font-semibold mb-1 flex items-center gap-2" ] [
                        Elem.img [ Attr.src "/logos/f1.png"; Attr.alt "F1"; cls "w-6 h-6" ]
                        Elem.span [] [ Text.raw "Formula 1" ]
                    ]
                    Elem.div [ cls "flex items-center gap-4 mt-2" ] [
                        if year > 2023 then
                            Elem.a [ Attr.href $"/f1?year={year - 1}"; cls "text-text-muted hover:text-text-primary text-sm" ] [
                                Text.raw $"← {year - 1}"
                            ]
                        Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw $"{year} Season Calendar" ]
                        if year < DateTime.UtcNow.Year + 1 then
                            Elem.a [ Attr.href $"/f1?year={year + 1}"; cls "text-text-muted hover:text-text-primary text-sm" ] [
                                Text.raw $"{year + 1} →"
                            ]
                    ]
                ]
                match nextRace with
                | Some nr ->
                    Elem.div [ cls "mb-6 p-4 rounded-xl border-2 border-[#E10600]/30 bg-gradient-to-r from-[#E10600]/5 to-transparent" ] [
                        Elem.div [ cls "text-xs text-text-muted uppercase mb-1" ] [ Text.raw "Next Race" ]
                        Elem.div [ cls "font-semibold text-text-primary" ] [
                            Text.raw $"Round {nr.RoundNumber}: "
                            Text.enc (if isNull nr.MeetingName then "" else nr.MeetingName)
                        ]
                        Elem.div [ cls "text-text-muted text-sm mt-1" ] [
                            if not (isNull nr.CircuitName) then
                                Text.enc nr.CircuitName
                                if not (isNull nr.Country) then
                                    Text.raw " · "
                            if not (isNull nr.Country) then
                                Text.enc nr.Country
                        ]
                        Elem.div [ cls "text-text-muted text-sm mt-1" ] [
                            FormatHelpers.scheduledTimeNode nr.ScheduledStart
                        ]
                    ]
                | None -> ()
                if races.Count = 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-lg p-12 text-center" ] [
                        Elem.p [ cls "text-text-muted" ] [ Text.raw $"No F1 races found for {year}." ]
                    ]
                else
                    Elem.div [ cls "space-y-2" ] [
                        for race in races do
                            raceRow race
                    ]
            ]
        ]
