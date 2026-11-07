namespace Backend.Rendering

open System
open System.Globalization
open System.Text.Json
open Falco.Markup
open backend.GraphQL.Types
open backend.Models
open backend.Services

module WorldCupRenderer =

    let private cls = Layout.cls
    let private homeColor = "#60A5FA"
    let private drawColor = "#F59E0B"
    let private awayColor = "#F87171"

    type private WorldCupCardView =
        { Id: string
          Sport: string
          HomeTeam: string
          AwayTeam: string
          HomeTeamAbbreviation: string
          AwayTeamAbbreviation: string
          HomeScore: Nullable<int>
          AwayScore: Nullable<int>
          ScheduledStart: DateTime
          Status: string
          IsOvertime: bool
          HomeProbability: Nullable<float>
          DrawProbability: Nullable<float>
          AwayProbability: Nullable<float>
          NailbiterScore: Nullable<float>
          Source: string
          GameClock: string
          Period: Nullable<int> }

    let private abbreviationOrName (abbr: string) (name: string) =
        if String.IsNullOrEmpty abbr then name else abbr

    let private flagFromIsoCountryCode (countryCode: string) =
        if String.IsNullOrWhiteSpace countryCode then ""
        else
            let code = countryCode.Trim().ToUpperInvariant()
            if code.Length <> 2 || code |> Seq.exists (Char.IsLetter >> not) then ""
            else
                code
                |> Seq.map (fun c -> Char.ConvertFromUtf32(0x1F1E6 + int c - int 'A'))
                |> String.concat ""

    let private regionalWorldCupFlags =
        dict [
            "ENG", "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F"
            "SCO", "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F"
            "WAL", "\U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F"
            "NIR", "🇬🇧"
            "KOS", "🇽🇰"
            "KVX", "🇽🇰"
            "TPE", "🇹🇼"
        ]

    let private fifaToIsoCountryCodes =
        dict [
            "ALG", "DZ"; "ANG", "AO"; "ARU", "AW"; "BAH", "BS"; "BER", "BM"; "BOL", "BO"
            "BUL", "BG"; "CAM", "KH"; "CAY", "KY"; "CGO", "CG"; "CHA", "TD"; "CHI", "CL"
            "CPV", "CV"; "CRC", "CR"; "CRO", "HR"; "CTA", "CF"; "DEN", "DK"; "EQG", "GQ"
            "FIJ", "FJ"; "GAM", "GM"; "GER", "DE"; "GRE", "GR"; "GUA", "GT"; "GUI", "GN"
            "GUM", "GU"; "HAI", "HT"; "HON", "HN"; "KUW", "KW"; "KYR", "KG"; "LES", "LS"
            "MAD", "MG"; "MAS", "MY"; "MON", "MC"; "MRI", "MU"; "MTN", "MR"; "NCA", "NI"
            "NED", "NL"; "NIG", "NE"; "PLE", "PS"; "POR", "PT"; "PUR", "PR"; "RSA", "ZA"
            "SEY", "SC"; "SIN", "SG"; "SUI", "CH"; "TAH", "PF"; "TAN", "TZ"; "TOG", "TG"
            "UAE", "AE"; "VAN", "VU"; "VIE", "VN"; "ZAM", "ZM"; "ZIM", "ZW"
        ]

    let private isoCountryCodesByThreeLetterName =
        lazy
            (CultureInfo.GetCultures(CultureTypes.SpecificCultures)
             |> Seq.choose (fun culture ->
                 try
                     let region = RegionInfo(culture.Name)
                     Some (region.ThreeLetterISORegionName.ToUpperInvariant(), region.TwoLetterISORegionName.ToUpperInvariant())
                 with _ -> None)
             |> Seq.distinctBy fst
             |> dict)

    let private isoCountryCodesByEnglishName =
        lazy
            (CultureInfo.GetCultures(CultureTypes.SpecificCultures)
             |> Seq.choose (fun culture ->
                 try
                     let region = RegionInfo(culture.Name)
                     Some (region.EnglishName.ToUpperInvariant(), region.TwoLetterISORegionName.ToUpperInvariant())
                 with _ -> None)
             |> Seq.distinctBy fst
             |> dict)

    let private countryFlag (abbr: string) (name: string) =
        let normalizedAbbr = if String.IsNullOrWhiteSpace abbr then "" else abbr.Trim().ToUpperInvariant()
        match regionalWorldCupFlags.TryGetValue(normalizedAbbr) with
        | true, flag -> flag
        | _ ->
            match fifaToIsoCountryCodes.TryGetValue(normalizedAbbr) with
            | true, countryCode -> flagFromIsoCountryCode countryCode
            | _ ->
                match isoCountryCodesByThreeLetterName.Value.TryGetValue(normalizedAbbr) with
                | true, countryCode -> flagFromIsoCountryCode countryCode
                | _ ->
                    let normalizedName = if String.IsNullOrWhiteSpace name then "" else name.Trim().ToUpperInvariant()
                    match isoCountryCodesByEnglishName.Value.TryGetValue(normalizedName) with
                    | true, countryCode -> flagFromIsoCountryCode countryCode
                    | _ -> ""

    let private viewFromCard (card: WorldCupCardData) =
        let game = card.Game
        { Id = game.Id
          Sport = game.Sport
          HomeTeam = game.HomeTeam
          AwayTeam = game.AwayTeam
          HomeTeamAbbreviation = abbreviationOrName game.HomeTeamAbbreviation game.HomeTeam
          AwayTeamAbbreviation = abbreviationOrName game.AwayTeamAbbreviation game.AwayTeam
          HomeScore = game.HomeScore
          AwayScore = game.AwayScore
          ScheduledStart = game.ScheduledStart
          Status = game.Status
          IsOvertime = game.IsOvertime
          HomeProbability = card.HomeProbability
          DrawProbability = card.DrawProbability
          AwayProbability = card.AwayProbability
          NailbiterScore = card.NailbiterScore
          Source = card.Source
          GameClock = card.GameClock
          Period = card.Period }

    let private calculateThreeWayClosenessScore home draw away =
        let maxOutcome = max home (max draw away)
        let rawScore = (1.0 - maxOutcome) / (2.0 / 3.0) * 100.0
        Math.Clamp(rawScore, 0.0, 100.0)

    let private viewFromUpdate (update: GameUpdate) =
        let homeAbbr = abbreviationOrName update.HomeTeamAbbreviation update.HomeTeam
        let awayAbbr = abbreviationOrName update.AwayTeamAbbreviation update.AwayTeam
        let nailbiterScore =
            if update.HomeWinProbability.HasValue && update.DrawWinProbability.HasValue && update.AwayWinProbability.HasValue then
                Nullable<float>(calculateThreeWayClosenessScore update.HomeWinProbability.Value update.DrawWinProbability.Value update.AwayWinProbability.Value)
            else Nullable<float>()

        { Id = update.GameId
          Sport = SportExtensions.ToDbValue(Sport.WORLD_CUP)
          HomeTeam = update.HomeTeam
          AwayTeam = update.AwayTeam
          HomeTeamAbbreviation = homeAbbr
          AwayTeamAbbreviation = awayAbbr
          HomeScore = update.HomeScore
          AwayScore = update.AwayScore
          ScheduledStart = update.UpdatedAt
          Status = GameStatusExtensions.ToDbValue(update.Status)
          IsOvertime = update.IsOvertime
          HomeProbability = update.HomeWinProbability
          DrawProbability = update.DrawWinProbability
          AwayProbability = update.AwayWinProbability
          NailbiterScore = nailbiterScore
          Source = update.OddsSource
          GameClock = update.GameClock
          Period = update.Period }

    let private hasThreeWayOdds (view: WorldCupCardView) =
        view.HomeProbability.HasValue && view.DrawProbability.HasValue && view.AwayProbability.HasValue

    let private hasAdvanceOdds (view: WorldCupCardView) =
        view.IsOvertime
        && view.HomeProbability.HasValue
        && not view.DrawProbability.HasValue
        && view.AwayProbability.HasValue

    let private matchUrl (view: WorldCupCardView) =
        $"/world-cup/match/{Uri.EscapeDataString(view.Id)}"

    let private roundedPercentages (home: float) (draw: float) =
        let homePct = int (Math.Round(home * 100.0))
        let drawPct = int (Math.Round(draw * 100.0))
        let awayPct = 100 - homePct - drawPct
        struct (homePct, drawPct, awayPct)

    let private roundedTwoWayPercentages (home: float) =
        let homePct = int (Math.Round(home * 100.0))
        struct (homePct, 100 - homePct)

    let private flagNode (abbr: string) (name: string) =
        let flag = countryFlag abbr name
        let flagTitle = if String.IsNullOrWhiteSpace name then abbr else name
        if String.IsNullOrEmpty flag then []
        else
            [ Elem.span [ cls "wc-flag"; Attr.create "aria-hidden" "true"; Attr.create "title" flagTitle ] [
                  Text.raw flag
              ] ]

    let private probabilityText (view: WorldCupCardView) probability rounded =
        if GameWinnerProbabilityPolicy.IsBound(view.Source) then FormatHelpers.formatProbability (Nullable probability)
        else $"{rounded}%%"

    let private accessibleProbability (percentage: string) =
        if percentage = ">99%" then "greater than 99 percent"
        elif percentage = "<1%" then "less than 1 percent"
        else percentage.TrimEnd('%') + " percent"

    let private probabilityLabel (abbr: string) (name: string) (percentage: string) =
        let flag = countryFlag abbr name
        let safeAbbr = System.Net.WebUtility.HtmlEncode(abbr)
        let safePercentage = System.Net.WebUtility.HtmlEncode(percentage)
        if String.IsNullOrEmpty flag then $"{safeAbbr} {safePercentage}"
        else $"{flag} {safeAbbr} {safePercentage}"

    let private teamLine (abbr: string) (name: string) (score: Nullable<int>) (color: string) =
        Elem.div [ cls "wc-team-line" ] [
            Elem.div [ cls "wc-team" ] [
                Elem.span [ cls "team-dot"; Attr.style $"background: {color}" ] []
                yield! flagNode abbr name
                Elem.span [ cls "wc-team-abbr" ] [ Text.enc abbr ]
            ]
            if score.HasValue then
                Elem.span [ cls "team-score" ] [ Text.raw $"{score.Value}" ]
        ]

    let private probabilityBarContent (view: WorldCupCardView) =
        [
            if view.Status <> "final" then
                if view.IsOvertime then
                    if hasAdvanceOdds view then
                        let h = view.HomeProbability.Value
                        let a = view.AwayProbability.Value
                        let struct (homeRounded, awayRounded) = roundedTwoWayPercentages h
                        let homePct = probabilityText view h homeRounded
                        let awayPct = probabilityText view a awayRounded
                        let homeName = if String.IsNullOrEmpty view.HomeTeam then view.HomeTeamAbbreviation else view.HomeTeam
                        let awayName = if String.IsNullOrEmpty view.AwayTeam then view.AwayTeamAbbreviation else view.AwayTeam
                        let aria = $"{homeName} to advance {accessibleProbability homePct}, {awayName} to advance {accessibleProbability awayPct}"
                        Elem.div [ cls "card-probability" ] [
                            Elem.div [ cls "wc-advance-label" ] [ Text.raw "To advance" ]
                            Elem.div [ cls "wc-advance-track"; Attr.create "role" "img"; Attr.create "aria-label" aria ] [
                                Elem.div [ cls "wc-segment"; Attr.style $"width: {h * 100.0:F1}%%; background: {homeColor}" ] []
                                Elem.div [ cls "wc-segment"; Attr.style $"width: {a * 100.0:F1}%%; background: {awayColor}" ] []
                                if h > 0.0 && a > 0.0 then ProbabilityBar.divider h
                            ]
                            Elem.div [ cls "wc-advance-labels" ] [
                                Elem.span [ cls "wc-prob-home" ] [ Text.raw (probabilityLabel view.HomeTeamAbbreviation view.HomeTeam homePct) ]
                                Elem.span [ cls "wc-prob-away" ] [ Text.raw (probabilityLabel view.AwayTeamAbbreviation view.AwayTeam awayPct) ]
                            ]
                        ]
                    else
                        Elem.div [ cls "wc-market-note" ] [
                            Text.raw "Advance odds unavailable"
                        ]
                elif hasThreeWayOdds view then
                    let h = view.HomeProbability.Value
                    let d = view.DrawProbability.Value
                    let a = view.AwayProbability.Value
                    let struct (homeRounded, drawRounded, awayRounded) = roundedPercentages h d
                    let homePct = probabilityText view h homeRounded
                    let drawPct = probabilityText view d drawRounded
                    let awayPct = probabilityText view a awayRounded
                    let homeName = if String.IsNullOrEmpty view.HomeTeam then view.HomeTeamAbbreviation else view.HomeTeam
                    let awayName = if String.IsNullOrEmpty view.AwayTeam then view.AwayTeamAbbreviation else view.AwayTeam
                    let aria = $"{homeName} {accessibleProbability homePct}, Draw {accessibleProbability drawPct}, {awayName} {accessibleProbability awayPct}"
                    Elem.div [ cls "card-probability" ] [
                        Elem.div [ cls "wc-track"; Attr.create "role" "img"; Attr.create "aria-label" aria ] [
                            Elem.div [ cls "wc-track-segment"; Attr.style $"width: {h * 100.0:F1}%%; background: {homeColor}" ] []
                            Elem.div [ cls "wc-track-segment"; Attr.style $"width: {d * 100.0:F1}%%; background: {drawColor}" ] []
                            Elem.div [ cls "wc-track-segment"; Attr.style $"width: {a * 100.0:F1}%%; background: {awayColor}" ] []
                        ]
                        Elem.div [ cls "wc-prob-labels" ] [
                            Elem.span [ cls "wc-prob-home"; Attr.style $"color: {homeColor}" ] [ Text.raw (probabilityLabel view.HomeTeamAbbreviation view.HomeTeam homePct) ]
                            Elem.span [ cls "wc-prob-draw"; Attr.style $"color: {drawColor}" ] [ Text.enc $"Draw {drawPct}" ]
                            Elem.span [ cls "wc-prob-away"; Attr.style $"color: {awayColor}" ] [ Text.raw (probabilityLabel view.AwayTeamAbbreviation view.AwayTeam awayPct) ]
                        ]
                    ]
                else
                    Elem.div [ cls "wc-market-note" ] [
                        Text.raw "3-way odds unavailable"
                    ]
        ]

    let private fragmentBlock (idPrefix: string) (view: WorldCupCardView) children =
        Elem.div [ Attr.id $"{idPrefix}{view.Id}" ] children

    let private probabilityBar (view: WorldCupCardView) =
        fragmentBlock "probbar-" view (probabilityBarContent view)

    let private matchProbabilityBarContent (view: WorldCupCardView) =
        [
            if view.Status = "final" then
                Elem.p [ cls "meta-text" ] [ Text.raw "Final matches no longer show live probability." ]
            else
                yield! probabilityBarContent view
        ]

    let private matchProbabilityBar (view: WorldCupCardView) =
        fragmentBlock "match-probbar-" view (matchProbabilityBarContent view)

    let private clockContent (view: WorldCupCardView) =
        [
            if view.Status = "in_progress" then
                let clockText = FormatHelpers.formatGameClock view.Period view.GameClock view.Sport
                Elem.div [ cls "live-clock" ] [
                    Text.raw (if String.IsNullOrEmpty clockText then "Live" else clockText)
                ]
        ]

    let private clock (view: WorldCupCardView) =
        fragmentBlock "clock-" view (clockContent view)

    let private matchClock (view: WorldCupCardView) =
        fragmentBlock "match-clock-" view (clockContent view)

    let private scoreBlockContent (view: WorldCupCardView) =
        [
            if view.Status <> "final" && view.NailbiterScore.HasValue && hasThreeWayOdds view then
                let level = FormatHelpers.getHypeLevel view.NailbiterScore
                let levelClass = FormatHelpers.hypeLevelClass level
                Elem.div [ cls "metric-block" ] [
                    Elem.span [ cls "metric-label" ] [ Text.raw "Nailbiter" ]
                    Elem.span [ cls $"metric-value {levelClass}" ] [
                        Text.raw $"{Math.Round(view.NailbiterScore.Value)}"
                    ]
                ]
        ]

    let private scoreBlock (view: WorldCupCardView) =
        fragmentBlock "excitement-" view (scoreBlockContent view)

    let private matchScoreBlock (view: WorldCupCardView) =
        fragmentBlock "match-excitement-" view (scoreBlockContent view)

    let private statusBadge (idPrefix: string) (view: WorldCupCardView) =
        let status = GameStatusExtensions.FromDbValue(view.Status)
        let statusLabel = if status.HasValue then FormatHelpers.getStatusLabel status.Value else view.Status
        let statusClass = if status.HasValue then FormatHelpers.getStatusClass status.Value else ""
        Elem.span [ cls $"status-badge {statusClass}"; Attr.id $"{idPrefix}{view.Id}" ] [ Text.raw statusLabel ]

    let private marketLabel (view: WorldCupCardView) =
        if view.IsOvertime then "To advance" else "Home / Draw / Away"

    let private cardChildren (view: WorldCupCardView) =
        [
            Elem.div [ cls "wc-card-header" ] [
                Elem.div [ cls "wc-card-league" ] [
                    Elem.img [ Attr.src (Logos.getLeagueLogoUrl Sport.WORLD_CUP); Attr.alt "World Cup"; cls "league-icon" ]
                    statusBadge "status-" view
                ]
                if view.Status = "scheduled" || view.Status = "final" then
                    Elem.span [ cls "meta-text" ] [ FormatHelpers.scheduledTimeNode view.ScheduledStart ]
            ]
            Elem.div [ cls "team-scores"; Attr.id $"scores-{view.Id}" ] [
                teamLine view.AwayTeamAbbreviation view.AwayTeam view.AwayScore awayColor
                teamLine view.HomeTeamAbbreviation view.HomeTeam view.HomeScore homeColor
            ]
            probabilityBar view
            clock view
            Elem.div [ cls "wc-card-footer" ] [
                Elem.span [ cls "wc-market-label"; Attr.id $"market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
                scoreBlock view
            ]
        ]

    let RenderCardLink (card: WorldCupCardData) =
        let view = viewFromCard card
        let liveClass = if view.Status = "in_progress" then " is-live" else ""
        Elem.a [ Attr.href (matchUrl view); cls $"wc-card{liveClass}" ] (cardChildren view)

    let RenderCardStatic (card: WorldCupCardData) =
        let view = viewFromCard card
        let liveClass = if view.Status = "in_progress" then " is-live" else ""
        Elem.div [ cls $"wc-card-static{liveClass}" ] (cardChildren view)

    let RenderPage (cards: Collections.Generic.List<WorldCupCardData>) =
        [
            Elem.div [ cls "page" ] [
                Elem.div [ cls "page-header" ] [
                    Elem.h1 [ cls "league-title" ] [
                        Elem.span [] [ Text.raw "⚽" ]
                        Elem.span [] [ Text.raw "FIFA World Cup" ]
                    ]
                    Elem.p [ cls "meta-text" ] [ Text.raw "Live-first match cards with home, draw, and away probabilities" ]
                ]
                if cards.Count = 0 then
                    Elem.div [ cls "empty-state" ] [
                        Elem.p [ cls "empty-state-text" ] [ Text.raw "No active World Cup matches are available right now." ]
                    ]
                else
                    Elem.div [ cls "wc-grid"; Attr.create "data-init" "@get('/sse/live/world_cup', {requestCancellation: 'cleanup'})" ] [
                        for card in cards do
                            RenderCardLink card
                    ]
            ]
        ]

    let private matchTeamColumn (abbr: string) (name: string) (score: Nullable<int>) (color: string) =
        let flag = countryFlag abbr name
        Elem.div [ cls "wc-match-team" ] [
            if not (String.IsNullOrEmpty flag) then
                Elem.span [ cls "wc-match-flag"; Attr.create "aria-hidden" "true" ] [ Text.raw flag ]
            Elem.div [ cls "wc-match-team-name" ] [
                Elem.span [ cls "team-dot"; Attr.style $"background: {color}" ] []
                Elem.span [ cls "wc-match-abbr" ] [ Text.enc abbr ]
            ]
            if not (String.IsNullOrEmpty name) then
                Elem.span [ cls "wc-match-name" ] [ Text.enc name ]
            if score.HasValue then
                Elem.span [ cls "detail-score" ] [ Text.raw $"{score.Value}" ]
        ]

    let private matchScores (view: WorldCupCardView) =
        Elem.div [ Attr.id $"match-scores-{view.Id}"; cls "wc-match-scores" ] [
            matchTeamColumn view.AwayTeamAbbreviation view.AwayTeam view.AwayScore awayColor
            Elem.div [ cls "wc-match-center" ] [
                if view.Status = "scheduled" then
                    Elem.span [ cls "scoreboard-vs" ] [ Text.raw "VS" ]
                    Elem.span [ cls "meta-text" ] [ FormatHelpers.scheduledTimeNode view.ScheduledStart ]
                else
                    Elem.span [ cls "scoreboard-vs" ] [ Text.raw "@" ]
            ]
            matchTeamColumn view.HomeTeamAbbreviation view.HomeTeam view.HomeScore homeColor
        ]

    let private moneylineText (moneyline: Nullable<int>) =
        if moneyline.HasValue then FormatHelpers.formatMoneyline moneyline.Value else "—"

    let private oddsRow (label: string) (abbr: string) (name: string) (moneyline: Nullable<int>) (color: string) =
        Elem.div [ cls "wc-odds-row" ] [
            Elem.div [ cls "wc-team" ] [
                Elem.span [ cls "team-dot"; Attr.style $"background: {color}" ] []
                yield! flagNode abbr name
                Elem.span [ cls "meta-text" ] [ Text.raw label ]
                Elem.span [ cls "wc-team-abbr" ] [ Text.enc abbr ]
            ]
            Elem.span [ cls "wc-odds-value" ] [ Text.raw (moneylineText moneyline) ]
        ]

    let private probabilityChart (view: WorldCupCardView) (snapshots: Collections.Generic.List<OddsSnapshot>) =
        let chartSnapshots =
            snapshots
            |> Seq.filter (fun s ->
                if view.IsOvertime then not s.DrawWinProbability.HasValue
                else s.DrawWinProbability.HasValue)
            |> Seq.toArray

        let chartTitle = if view.IsOvertime then "Advance Probability History" else "Probability History"
        let chartDescription =
            if view.IsOvertime then "Probability of each team advancing over time"
            else "Home, draw, and away movement over time"

        Elem.div [ cls "panel" ] [
            Elem.h2 [ cls "panel-title-tight" ] [ Text.raw chartTitle ]
            Elem.p [ cls "panel-subtitle" ] [ Text.raw chartDescription ]
            if chartSnapshots.Length >= 2 then
                let chartData =
                    chartSnapshots
                    |> Array.map (fun s ->
                        {| homeWinProbability = s.HomeWinProbability
                           drawWinProbability = s.DrawWinProbability
                           awayWinProbability = s.AwayWinProbability
                           source = s.Source
                           isProbabilityBound = GameWinnerProbabilityPolicy.IsBound(s.Source)
                           timestamp = s.Timestamp.ToString("o")
                           label = FormatHelpers.formatGameClock s.Period s.GameClock view.Sport
                           homeScore = s.HomeScore
                           awayScore = s.AwayScore |})

                let chartJson = JsonSerializer.Serialize(chartData, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
                let chartJsonEncoded = System.Net.WebUtility.HtmlEncode(chartJson)
                Elem.create "nb-win-probability-chart" [
                    Attr.create "game-id" view.Id
                    Attr.create "data-live-final" (if view.Status = "final" then "true" else "false")
                    Attr.create "data" chartJsonEncoded
                    Attr.create "home-color" homeColor
                    Attr.create "draw-color" drawColor
                    Attr.create "away-color" awayColor
                    Attr.create "home-abbr" (if view.IsOvertime then $"{view.HomeTeamAbbreviation} advances" else view.HomeTeamAbbreviation)
                    Attr.create "draw-abbr" "Draw"
                    Attr.create "away-abbr" (if view.IsOvertime then $"{view.AwayTeamAbbreviation} advances" else view.AwayTeamAbbreviation)
                    cls "odds-chart"
                ] []
            else
                Elem.div [ cls "wc-chart-empty" ] [
                    Text.raw "Graph appears once live odds history has at least two updates."
                ]
        ]

    let RenderMatchPage (card: WorldCupCardData) (snapshots: Collections.Generic.List<OddsSnapshot>) =
        let view = viewFromCard card
        [
            Elem.div [ cls "page-narrow"; Attr.create "data-live-terminal" (if view.Status = "final" then "true" else "false") ] [
                Elem.a [ Attr.href "/world-cup"; cls "back-link" ] [ Text.raw "← All World Cup matches" ]
                Elem.div [ cls "wc-match-header" ] [
                    Elem.div [ cls "wc-match-eyebrow" ] [
                        Elem.img [ Attr.src (Logos.getLeagueLogoUrl Sport.WORLD_CUP); Attr.alt "World Cup"; cls "detail-league-logo" ]
                        Elem.span [ cls "meta-text" ] [ Text.raw "FIFA World Cup" ]
                    ]
                    Elem.h1 [ cls "wc-match-title" ] [
                        Text.enc $"{view.AwayTeamAbbreviation} at {view.HomeTeamAbbreviation}"
                    ]
                    Elem.p [ cls "wc-match-subtitle" ] [
                        Text.enc $"{view.AwayTeam} at {view.HomeTeam}"
                    ]
                ]
                Elem.div [ cls "wc-match-layout" ] [
                    Elem.div [ cls "wc-match-main" ] [
                        Elem.div [ cls "panel" ] [
                            Elem.div [ cls "detail-topbar" ] [
                                statusBadge "match-status-" view
                                if view.Status = "scheduled" || view.Status = "final" then
                                    Elem.span [ cls "meta-text" ] [ FormatHelpers.scheduledTimeNode view.ScheduledStart ]
                            ]
                            matchScores view
                            matchClock view
                        ]
                        Elem.div [ cls "panel" ] [
                            Elem.h2 [ cls "panel-title"; Attr.id $"match-market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
                            matchProbabilityBar view
                        ]
                        probabilityChart view snapshots
                    ]
                    Elem.div [ cls "wc-match-aside" ] [
                        Elem.div [ cls "wc-side-panel" ] [
                            Elem.div [ cls "wc-score-header" ] [
                                Elem.div [] [
                                    Elem.h2 [ cls "wc-score-title" ] [ Text.raw "Nailbiter" ]
                                    Elem.p [ cls "wc-score-subtitle" ] [ Text.raw (if view.IsOvertime then "Unavailable for advance odds" else "3-way closeness score") ]
                                ]
                                matchScoreBlock view
                            ]
                            if not (view.NailbiterScore.HasValue && hasThreeWayOdds view && view.Status <> "final") then
                                Elem.p [ cls "wc-score-note" ] [
                                    Text.raw (if view.IsOvertime then "Score is unavailable for two-way advance odds." else "Score unavailable until complete 3-way odds are available.")
                                ]
                        ]
                        Elem.div [ cls "wc-side-panel" ] [
                            Elem.h2 [ cls "panel-title-tight" ] [ Text.raw (if view.IsOvertime then "To advance" else "Moneyline") ]
                            Elem.p [ cls "panel-subtitle" ] [ Text.raw (if view.IsOvertime then "Latest live advance odds" else "Latest complete 3-way odds at page load") ]
                            oddsRow "Away" view.AwayTeamAbbreviation view.AwayTeam card.AwayMoneyline awayColor
                            if not view.IsOvertime then
                                oddsRow "Draw" "DRAW" "Draw" card.DrawMoneyline drawColor
                            oddsRow "Home" view.HomeTeamAbbreviation view.HomeTeam card.HomeMoneyline homeColor
                            if not (String.IsNullOrEmpty card.Source) || card.OddsTimestamp.HasValue then
                                Elem.div [ cls "wc-odds-note" ] [
                                    if not (String.IsNullOrEmpty card.Source) then
                                        Text.enc card.Source
                                    if not (String.IsNullOrEmpty card.Source) && card.OddsTimestamp.HasValue then
                                        Text.raw " · "
                                    if card.OddsTimestamp.HasValue then
                                        Text.raw (card.OddsTimestamp.Value.ToString("MMM d, h:mm tt"))
                                ]
                        ]
                    ]
                ]
            ]
        ]

    let RenderFragments (update: GameUpdate) =
        let view = viewFromUpdate update
        [
            Elem.div [ Attr.id $"scores-{update.GameId}"; cls "team-scores" ] [
                teamLine view.AwayTeamAbbreviation view.AwayTeam update.AwayScore awayColor
                teamLine view.HomeTeamAbbreviation view.HomeTeam update.HomeScore homeColor
            ]
            probabilityBar view
            clock view
            scoreBlock view
            Elem.span [ cls "wc-market-label"; Attr.id $"market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
            statusBadge "status-" view
        ]
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""

    let RenderMatchFragments (update: GameUpdate) =
        let view = viewFromUpdate update
        [
            matchScores view
            Elem.h2 [ cls "panel-title"; Attr.id $"match-market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
            matchProbabilityBar view
            matchClock view
            matchScoreBlock view
            statusBadge "match-status-" view
        ]
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""
