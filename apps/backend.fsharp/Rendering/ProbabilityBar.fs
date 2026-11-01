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
            cls "probability-divider absolute top-0 h-full"
            Attr.style $"left: {percent probability}; width: 2px; transform: translateX(-50%%); background: #fafafa; box-shadow: 0 0 0 1px #18181b"
        ] []

    let render (compact: bool) (homeAbbr: string) (awayAbbr: string)
        (homeProbability: float) (awayProbability: float) (colors: TeamColors.MatchupColors) =
        let label abbr probability = $"{abbr} {FormatHelpers.formatProbability (Nullable probability)}"
        let homeLabel = label homeAbbr homeProbability
        let awayLabel = label awayAbbr awayProbability
        Elem.div [ Attr.create "role" "group"; Attr.create "aria-label" "Win probability" ] [
            if not compact then
                Elem.div [ cls "text-sm text-text-secondary text-center mb-1" ] [ Text.raw "Win Probability" ]
            Elem.div [ cls (if compact then "flex justify-between gap-2 text-xs font-medium tabular-nums text-text-secondary mb-1" else "flex justify-between gap-2 text-sm font-medium tabular-nums text-text-secondary mb-1") ] [
                Elem.span [] [ Text.enc awayLabel ]
                Elem.span [ cls "text-right" ] [ Text.enc homeLabel ]
            ]
            Elem.div [
                cls (if compact then "probability-track relative h-1.5 rounded-full overflow-hidden flex" else "probability-track relative h-3 rounded-full overflow-hidden flex")
                Attr.create "aria-hidden" "true"
            ] [
                Elem.div [ cls "probability-away h-full flex-shrink-0"; Attr.style $"width: {percent awayProbability}; background: {colors.Away}" ] []
                Elem.div [ cls "probability-home h-full flex-shrink-0"; Attr.style $"width: {percent homeProbability}; background: {colors.Home}" ] []
                if awayProbability > 0.0 && homeProbability > 0.0 then divider awayProbability
            ]
        ]
