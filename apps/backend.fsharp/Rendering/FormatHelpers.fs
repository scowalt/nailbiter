namespace Backend.Rendering

open System
open Falco.Markup
open backend.GraphQL.Types

module FormatHelpers =

    let formatProbability (prob: Nullable<float>) =
        if not prob.HasValue then "-"
        elif prob.Value > 0.0 && prob.Value < 0.01 then "<1%"
        elif prob.Value > 0.99 && prob.Value < 1.0 then ">99%"
        else $"{Math.Round(prob.Value * 100.0)}%%"

    let formatPlayoffOutlook isFinal (upperBound: float option) (prob: float) =
        if isFinal && prob = 1.0 then "Clinched playoffs"
        elif isFinal && prob = 0.0 then "Missed playoffs"
        else
            match upperBound with
            | Some bound -> $"<{max 1.0 (Math.Ceiling(Math.Round(bound * 100.0, 6)))}%%"
            | None when prob < 0.01 -> "<1%"
            | None when prob > 0.99 -> ">99%"
            | None -> $"{prob:P0}"

    let pollRank (poll: Nullable<backend.Models.CollegePoll>) (rank: Nullable<int>) =
        [ if poll.HasValue && rank.HasValue && rank.Value >= 1 && rank.Value <= 25 then
            let label = (match poll.Value with backend.Models.CollegePoll.Cfp -> "CFP" | _ -> "AP") + $" rank {rank.Value}"
            Elem.sup [ Attr.class' "poll-rank"
                       Attr.title label
                       Attr.create "aria-label" label ] [ Text.raw $"{rank.Value}" ] ]

    let formatExcitement (score: Nullable<float>) =
        if score.HasValue then $"{Math.Round(score.Value)}"
        else "-"

    type ExcitementLevel = High | Medium | Low

    let getExcitementLevel (score: Nullable<float>) =
        if not score.HasValue || score.Value < 30.0 then Low
        elif score.Value < 60.0 then Medium
        else High

    let excitementLevelClass = function
        | High -> "excitement-high"
        | Medium -> "excitement-medium"
        | Low -> "excitement-low"

    let formatHype (score: Nullable<float>) =
        if score.HasValue then $"{Math.Round(score.Value)}"
        else "-"

    type HypeLevel = HypeHigh | HypeMedium | HypeLow

    let getHypeLevel (score: Nullable<float>) =
        if not score.HasValue || score.Value < 40.0 then HypeLow
        elif score.Value < 70.0 then HypeMedium
        else HypeHigh

    let hypeLevelClass = function
        | HypeHigh -> "hype-high"
        | HypeMedium -> "hype-medium"
        | HypeLow -> "hype-low"

    let formatGameClock (period: Nullable<int>) (gameClock: string) (sport: string) =
        if not period.HasValue then ""
        else
            let p = period.Value
            match sport.ToUpperInvariant() with
            | "CFB" ->
                if p > 4 then (if p = 5 then "OT" else $"{p - 4}OT")
                elif p > 0 then
                    if String.IsNullOrEmpty gameClock then $"Q{p}" else $"Q{p} {gameClock}"
                else ""
            | "MLB" ->
                if p <= 9 then $"Inning {p}" else $"Extra {p}"
            | "NHL" ->
                let periodLabel =
                    if p <= 3 then $"P{p}"
                    elif p = 4 then "OT"
                    else $"{p - 3}OT"
                if not (String.IsNullOrEmpty gameClock) && gameClock <> "0:00" then
                    $"{periodLabel} {gameClock}"
                else periodLabel
            | "WORLD_CUP" | "SOCCER" ->
                let periodLabel =
                    if p <= 2 then $"H{p}"
                    elif p = 3 then "ET"
                    else $"ET{p - 2}"
                if not (String.IsNullOrEmpty gameClock) && gameClock <> "0:00" then
                    $"{periodLabel} {gameClock}"
                else periodLabel
            | _ ->
                let periodLabel =
                    if p <= 4 then $"Q{p}" else $"OT{p - 4}"
                if not (String.IsNullOrEmpty gameClock) then
                    $"{periodLabel} {gameClock}"
                else periodLabel

    let collegeContext (game: backend.Models.Game) =
        [ if game.Sport = "cfb" then
            let label =
                match game.CollegeCompetition with
                | "cfp" -> Some "CFP elimination game"
                | "conference_championship" -> Some "Conference championship"
                | "bowl" -> Some "Non-CFP bowl"
                | _ -> None
            match label with
            | Some label -> Elem.div [ Attr.class' "college-context" ] [ Text.enc label ]
            | None -> ()
            if game.IsHistoricalImport then
                Elem.div [ Attr.class' "college-context" ] [ Text.raw "Historical import · hype and excitement unavailable" ] ]

    let formatScheduledTime (dt: DateTime) =
        dt.ToString("MMM d, h:mm tt", Globalization.CultureInfo.InvariantCulture)

    let scheduledTimeNode (dt: DateTime) =
        let utcDt = DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        let iso = utcDt.ToString("o")
        let fallback = formatScheduledTime dt
        Elem.time [ Attr.create "datetime" iso; Attr.create "class" "nb-localtime" ] [
            Text.raw fallback
        ]

    let broadcastLabel (networks: string[]) =
        let usable =
            if isNull networks then [||]
            else networks |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.map (fun n -> n.Trim())
        match usable with
        | [||] -> None
        | [| only |] -> Some only
        | many -> Some $"{many[0]} +{many.Length - 1}"

    let broadcastNodes (game: backend.Models.Game) =
        match game.Status with
        | "scheduled" | "in_progress" ->
            match broadcastLabel game.BroadcastNetworks with
            | Some label ->
                let title = String.Join(", ", game.BroadcastNetworks |> Array.filter (String.IsNullOrWhiteSpace >> not))
                [ Elem.span [ Attr.class' "nb-broadcast-sep"; Attr.create "aria-hidden" "true" ] [ Text.raw " · " ]
                  Elem.span [ Attr.class' "nb-broadcast"; Attr.id $"broadcast-{game.Id}"; Attr.title $"TV: {title}" ] [
                      Elem.span [ Attr.class' "visually-hidden" ] [ Text.raw "TV: " ]
                      Text.enc label ] ]
            | None -> []
        | _ -> []

    let formatScheduledTimeFromIso (isoString: string) =
        match DateTime.TryParse(isoString) with
        | true, dt -> formatScheduledTime dt
        | _ -> isoString

    let getStatusLabel (status: GameStatus) =
        match status with
        | GameStatus.IN_PROGRESS -> "Live"
        | GameStatus.SCHEDULED -> "Scheduled"
        | GameStatus.FINAL -> "Final"
        | GameStatus.CANCELED -> "Canceled"
        | _ -> status.ToString()

    let getStatusClass (status: GameStatus) =
        match status with
        | GameStatus.IN_PROGRESS -> "status-live animate-live-pulse"
        | GameStatus.SCHEDULED -> "status-scheduled"
        | GameStatus.FINAL -> "status-final"
        | GameStatus.CANCELED -> "status-final"
        | _ -> ""

    let formatMoneyline (odds: int) =
        if odds > 0 then $"+{odds}" else $"{odds}"

    let formatSpread (spread: float) (teamAbbr: string) =
        let sign = if spread > 0.0 then "+" else ""
        $"{teamAbbr} {sign}{spread}"

    let formatLapCount (completedLaps: Nullable<int>) (totalLaps: int) =
        if completedLaps.HasValue then $"Lap {completedLaps.Value}/{totalLaps}"
        else $"{totalLaps} laps"

    let formatGap (gap: Nullable<float>) =
        if not gap.HasValue then "-"
        elif gap.Value = 0.0 then "Leader"
        else $"+{gap.Value:F3}s"

    let formatTrackStatus (status: string) =
        if String.IsNullOrEmpty status then ""
        else
            match status.ToLowerInvariant() with
            | "green" -> "Green Flag"
            | "yellow" -> "Yellow Flag"
            | "sc" -> "Safety Car"
            | "vsc" -> "Virtual Safety Car"
            | "red" -> "Red Flag"
            | _ -> status

    let formatLapTime (seconds: Nullable<float>) =
        if not seconds.HasValue then "-"
        else
            let s = seconds.Value
            let mins = int (Math.Floor(s / 60.0))
            let secs = s % 60.0
            $"{mins}:{secs:F3}".PadLeft(mins.ToString().Length + 7, '0')

    let formatLapTimeFixed (seconds: Nullable<float>) =
        if not seconds.HasValue then "-"
        else
            let s = seconds.Value
            let mins = int (Math.Floor(s / 60.0))
            let secs = s % 60.0
            let secsStr = $"{secs:F3}"
            let secsStr = if secs < 10.0 then "0" + secsStr else secsStr
            $"{mins}:{secsStr}"
