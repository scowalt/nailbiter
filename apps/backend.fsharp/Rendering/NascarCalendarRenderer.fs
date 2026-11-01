namespace Backend.Rendering

open System
open Falco.Markup
open backend.Models

module NascarCalendarRenderer =

    let private cls = Layout.cls

    let private raceRow (race: NascarRace) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"
        let liveClass = if isLive then " border-[#CF0A2C]/30 bg-[#CF0A2C]/5" else ""

        Elem.a [ Attr.href $"/nascar/race/{race.Id}"; cls $"block bg-bg-card border border-border rounded-lg p-4 hover:border-border-hover transition-colors group{liveClass}" ] [
            Elem.div [ cls "flex items-center justify-between" ] [
                Elem.div [ cls "flex items-center gap-3 min-w-0 flex-1" ] [
                    Elem.span [ cls "text-text-muted text-sm tabular-nums w-8" ] [
                        Text.raw $"R{race.RaceNumber}"
                    ]
                    Elem.div [ cls "min-w-0" ] [
                        Elem.span [ cls "font-medium text-text-primary group-hover:text-accent transition-colors" ] [
                            Text.enc (if isNull race.RaceName then "" else race.RaceName)
                        ]
                    ]
                ]
                Elem.div [ cls "flex items-center gap-3 flex-shrink-0 ml-4" ] [
                    if isLive then
                        Elem.span [ cls "text-xs font-medium px-1.5 py-0.5 rounded bg-status-live/20 text-status-live" ] [ Text.raw "LIVE" ]
                        if race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                            Elem.span [ cls "text-text-muted text-sm" ] [
                                Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                            ]
                    elif isFinal then
                        Elem.span [ cls "text-xs font-medium px-1.5 py-0.5 rounded bg-status-final/20 text-status-final" ] [ Text.raw "Final" ]
                    else
                        Elem.span [ cls "text-text-muted text-sm" ] [
                            FormatHelpers.scheduledTimeNode race.ScheduledStart
                        ]
                    Elem.span [ cls "text-text-muted group-hover:text-text-primary transition-colors" ] [
                        Text.raw "→"
                    ]
                ]
            ]
        ]

    let Render (year: int) (races: Collections.Generic.List<NascarRace>) (logoPath: string) (title: string) (brandColor: string) (basePath: string) (yearFloor: int) =
        let nextRace =
            races
            |> Seq.tryFind (fun r -> r.Status = "scheduled" && r.ScheduledStart > DateTime.UtcNow)

        [
            Elem.div [ cls "max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8" ] [
                Elem.div [ cls "mb-8" ] [
                    Elem.h1 [ cls "text-2xl font-semibold mb-1 flex items-center gap-2" ] [
                        Elem.img [ Attr.src logoPath; Attr.alt title; cls "w-6 h-6" ]
                        Elem.span [] [ Text.raw title ]
                    ]
                    Elem.div [ cls "flex items-center gap-4 mt-2" ] [
                        if year > yearFloor then
                            Elem.a [ Attr.href $"{basePath}?year={year - 1}"; cls "text-text-muted hover:text-text-primary text-sm" ] [
                                Text.raw $"← {year - 1}"
                            ]
                        Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw $"{year} Season Calendar" ]
                        if year < DateTime.UtcNow.Year + 1 then
                            Elem.a [ Attr.href $"{basePath}?year={year + 1}"; cls "text-text-muted hover:text-text-primary text-sm" ] [
                                Text.raw $"{year + 1} →"
                            ]
                    ]
                ]
                match nextRace with
                | Some nr ->
                    Elem.div [ cls $"mb-6 p-4 rounded-xl border-2 bg-gradient-to-r to-transparent"; Attr.style $"border-color: {brandColor}4D; background: linear-gradient(to right, {brandColor}0D, transparent)" ] [
                        Elem.div [ cls "text-xs text-text-muted uppercase mb-1" ] [ Text.raw "Next Race" ]
                        Elem.div [ cls "font-semibold text-text-primary" ] [
                            Text.raw $"Race {nr.RaceNumber}: "
                            Text.enc (if isNull nr.RaceName then "" else nr.RaceName)
                        ]
                        Elem.div [ cls "text-text-muted text-sm mt-1" ] [
                            FormatHelpers.scheduledTimeNode nr.ScheduledStart
                        ]
                    ]
                | None -> ()
                if races.Count = 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-lg p-12 text-center" ] [
                        Elem.p [ cls "text-text-muted" ] [ Text.raw $"No races found for {year}." ]
                    ]
                else
                    Elem.div [ cls "space-y-2" ] [
                        for race in races do
                            raceRow race
                    ]
            ]
        ]
