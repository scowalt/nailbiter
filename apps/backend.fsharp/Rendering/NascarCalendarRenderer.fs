namespace Backend.Rendering

open System
open Falco.Markup
open backend.Models

module NascarCalendarRenderer =

    let private cls = Layout.cls

    let private raceRow (race: NascarRace) =
        let isLive = race.Status = "in_progress"
        let isFinal = race.Status = "final"

        Elem.a [ Attr.href $"/nascar/race/{race.Id}"; cls "race-card" ] [
            Elem.div [ cls "race-card-body" ] [
                Elem.div [ cls "race-card-main" ] [
                    Elem.span [ cls "race-number" ] [
                        Text.raw $"R{race.RaceNumber}"
                    ]
                    Elem.div [ cls "race-card-text" ] [
                        Elem.span [ cls "race-name" ] [
                            Text.enc (if isNull race.RaceName then "" else race.RaceName)
                        ]
                    ]
                ]
                Elem.div [ cls "race-card-aside" ] [
                    if isLive then
                        Elem.span [ cls "race-badge-live" ] [ Text.raw "LIVE" ]
                        if race.TotalLaps > 0 && race.CompletedLaps.HasValue then
                            Elem.span [ cls "meta-text" ] [
                                Text.raw $"Lap {race.CompletedLaps.Value}/{race.TotalLaps}"
                            ]
                    elif isFinal then
                        Elem.span [ cls "race-badge-final" ] [ Text.raw "Final" ]
                    else
                        Elem.span [ cls "meta-text" ] [
                            FormatHelpers.scheduledTimeNode race.ScheduledStart
                        ]
                    Elem.span [ cls "race-card-arrow" ] [
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
            Elem.div [ cls "page" ] [
                Elem.div [ cls "page-header" ] [
                    Elem.h1 [ cls "league-title" ] [
                        Elem.img [ Attr.src logoPath; Attr.alt title; cls "league-title-logo" ]
                        Elem.span [] [ Text.raw title ]
                    ]
                    Elem.div [ cls "week-nav" ] [
                        if year > yearFloor then
                            Elem.a [ Attr.href $"{basePath}?year={year - 1}"; cls "week-nav-link" ] [
                                Text.raw $"← {year - 1}"
                            ]
                        Elem.span [ cls "meta-text" ] [ Text.raw $"{year} Season Calendar" ]
                        if year < DateTime.UtcNow.Year + 1 then
                            Elem.a [ Attr.href $"{basePath}?year={year + 1}"; cls "week-nav-link" ] [
                                Text.raw $"{year + 1} →"
                            ]
                    ]
                ]
                match nextRace with
                | Some nr ->
                    Elem.div [ cls $"next-race"; Attr.style $"border-color: {brandColor}4D; background: linear-gradient(to right, {brandColor}0D, transparent)" ] [
                        Elem.div [ cls "next-race-label" ] [ Text.raw "Next Race" ]
                        Elem.div [ cls "next-race-name" ] [
                            Text.raw $"Race {nr.RaceNumber}: "
                            Text.enc (if isNull nr.RaceName then "" else nr.RaceName)
                        ]
                        Elem.div [ cls "next-race-meta" ] [
                            FormatHelpers.scheduledTimeNode nr.ScheduledStart
                        ]
                    ]
                | None -> ()
                if races.Count = 0 then
                    Elem.div [ cls "empty-state" ] [
                        Elem.p [ cls "empty-state-text" ] [ Text.raw $"No races found for {year}." ]
                    ]
                else
                    Elem.div [ cls "race-list" ] [
                        for race in races do
                            raceRow race
                    ]
            ]
        ]
