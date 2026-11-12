namespace Backend.Rendering

open System
open System.Globalization
open Falco.Markup

module ProbabilityBar =
    let private cls = Layout.cls
    let private percent (probability: float) =
        (probability * 100.0).ToString("0.####", CultureInfo.InvariantCulture) + "%"

    let divider probability =
        Elem.div [
            cls "probability-divider"
            Attr.style $"left: {percent probability}; width: 2px; transform: translateX(-50%%); background: var(--color-text-primary); box-shadow: 0 0 0 1px var(--color-bg-card)"
        ] []

    let render (compact: bool) (homeAbbr: string) (awayAbbr: string)
        (homeProbability: float) (awayProbability: float) (colors: TeamColors.MatchupColors) =
        let label abbr probability = $"{abbr} {FormatHelpers.formatProbability (Nullable probability)}"
        let homeLabel = label homeAbbr homeProbability
        let awayLabel = label awayAbbr awayProbability
        Elem.div [ Attr.create "role" "group"; Attr.create "aria-label" "Win probability" ] [
            if not compact then
                Elem.div [ cls "probability-title" ] [ Text.raw "Win Probability" ]
            Elem.div [ cls (if compact then "probability-labels is-compact" else "probability-labels") ] [
                Elem.span [] [ Text.enc awayLabel ]
                Elem.span [ cls "probability-home-label" ] [ Text.enc homeLabel ]
            ]
            Elem.div [
                cls (if compact then "probability-track is-compact" else "probability-track")
                Attr.create "aria-hidden" "true"
            ] [
                Elem.div [ cls "probability-away"; Attr.style $"width: {percent awayProbability}; background: {colors.Away}" ] []
                Elem.div [ cls "probability-home"; Attr.style $"width: {percent homeProbability}; background: {colors.Home}" ] []
                if awayProbability > 0.0 && homeProbability > 0.0 then divider awayProbability
            ]
        ]
