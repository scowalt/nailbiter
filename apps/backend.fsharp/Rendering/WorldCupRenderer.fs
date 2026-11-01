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
            [ Elem.span [ cls "w-5 text-base leading-none text-center flex-shrink-0"; Attr.create "aria-hidden" "true"; Attr.create "title" flagTitle ] [
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
        Elem.div [ cls "flex items-center justify-between gap-3" ] [
            Elem.div [ cls "flex items-center gap-2 min-w-0" ] [
                Elem.span [ cls "inline-block w-2 h-2 rounded-full flex-shrink-0"; Attr.style $"background: {color}" ] []
                yield! flagNode abbr name
                Elem.span [ cls "font-medium text-text-primary truncate" ] [ Text.enc abbr ]
            ]
            if score.HasValue then
                Elem.span [ cls "text-lg font-semibold tabular-nums text-text-primary" ] [ Text.raw $"{score.Value}" ]
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
                        Elem.div [ cls "mt-3" ] [
                            Elem.div [ cls "text-xs text-text-muted mb-1" ] [ Text.raw "To advance" ]
                            Elem.div [ cls "relative h-2 rounded-full overflow-hidden flex"; Attr.create "role" "img"; Attr.create "aria-label" aria ] [
                                Elem.div [ cls "h-full"; Attr.style $"width: {h * 100.0:F1}%%; background: {homeColor}" ] []
                                Elem.div [ cls "h-full"; Attr.style $"width: {a * 100.0:F1}%%; background: {awayColor}" ] []
                                if h > 0.0 && a > 0.0 then ProbabilityBar.divider h
                            ]
                            Elem.div [ cls "grid grid-cols-2 gap-2 text-xs font-medium tabular-nums text-text-secondary mt-1" ] [
                                Elem.span [ cls "truncate" ] [ Text.raw (probabilityLabel view.HomeTeamAbbreviation view.HomeTeam homePct) ]
                                Elem.span [ cls "text-right truncate" ] [ Text.raw (probabilityLabel view.AwayTeamAbbreviation view.AwayTeam awayPct) ]
                            ]
                        ]
                    else
                        Elem.div [ cls "mt-3 rounded-md border border-border bg-bg-card-hover/40 px-3 py-2 text-xs text-text-muted text-center" ] [
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
                    Elem.div [ cls "mt-3" ] [
                        Elem.div [ cls "h-2 rounded-full overflow-hidden flex"; Attr.create "role" "img"; Attr.create "aria-label" aria ] [
                            Elem.div [ cls "h-full transition-all duration-300"; Attr.style $"width: {h * 100.0:F1}%%; background: {homeColor}" ] []
                            Elem.div [ cls "h-full transition-all duration-300"; Attr.style $"width: {d * 100.0:F1}%%; background: {drawColor}" ] []
                            Elem.div [ cls "h-full transition-all duration-300"; Attr.style $"width: {a * 100.0:F1}%%; background: {awayColor}" ] []
                        ]
                        Elem.div [ cls "grid grid-cols-3 gap-2 text-xs font-medium tabular-nums mt-1" ] [
                            Elem.span [ cls "truncate"; Attr.style $"color: {homeColor}" ] [ Text.raw (probabilityLabel view.HomeTeamAbbreviation view.HomeTeam homePct) ]
                            Elem.span [ cls "text-center truncate"; Attr.style $"color: {drawColor}" ] [ Text.enc $"Draw {drawPct}" ]
                            Elem.span [ cls "text-right truncate"; Attr.style $"color: {awayColor}" ] [ Text.raw (probabilityLabel view.AwayTeamAbbreviation view.AwayTeam awayPct) ]
                        ]
                    ]
                else
                    Elem.div [ cls "mt-3 rounded-md border border-border bg-bg-card-hover/40 px-3 py-2 text-xs text-text-muted text-center" ] [
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
                Elem.p [ cls "text-sm text-text-muted" ] [ Text.raw "Final matches no longer show live probability." ]
            else
                yield! probabilityBarContent view
        ]

    let private matchProbabilityBar (view: WorldCupCardView) =
        fragmentBlock "match-probbar-" view (matchProbabilityBarContent view)

    let private clockContent (view: WorldCupCardView) =
        [
            if view.Status = "in_progress" then
                let clockText = FormatHelpers.formatGameClock view.Period view.GameClock view.Sport
                Elem.div [ cls "mt-2 text-sm text-status-live font-medium text-center" ] [
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
                Elem.div [ cls "text-right" ] [
                    Elem.span [ cls "block text-xs text-text-muted mb-0.5" ] [ Text.raw "Nailbiter" ]
                    Elem.span [ cls $"text-xl font-semibold tabular-nums {levelClass}" ] [
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
        Elem.span [ cls $"text-xs font-medium px-1.5 py-0.5 rounded {statusClass}"; Attr.id $"{idPrefix}{view.Id}" ] [ Text.raw statusLabel ]

    let private marketLabel (view: WorldCupCardView) =
        if view.IsOvertime then "To advance" else "Home / Draw / Away"

    let private cardChildren (view: WorldCupCardView) =
        [
            Elem.div [ cls "flex items-center justify-between mb-3" ] [
                Elem.div [ cls "flex items-center gap-2" ] [
                    Elem.img [ Attr.src (Logos.getLeagueLogoUrl Sport.WORLD_CUP); Attr.alt "World Cup"; cls "w-4 h-4" ]
                    statusBadge "status-" view
                ]
                if view.Status = "scheduled" || view.Status = "final" then
                    Elem.span [ cls "text-sm text-text-muted" ] [ FormatHelpers.scheduledTimeNode view.ScheduledStart ]
            ]
            Elem.div [ cls "space-y-2"; Attr.id $"scores-{view.Id}" ] [
                teamLine view.AwayTeamAbbreviation view.AwayTeam view.AwayScore awayColor
                teamLine view.HomeTeamAbbreviation view.HomeTeam view.HomeScore homeColor
            ]
            probabilityBar view
            clock view
            Elem.div [ cls "flex justify-between items-end pt-2 mt-2 border-t border-border" ] [
                Elem.span [ cls "text-xs text-text-muted"; Attr.id $"market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
                scoreBlock view
            ]
        ]

    let RenderCardLink (card: WorldCupCardData) =
        let view = viewFromCard card
        let liveClass = if view.Status = "in_progress" then " border-status-live/30" else ""
        Elem.a [ Attr.href (matchUrl view); cls $"block bg-bg-card border border-border rounded-lg p-4 hover:border-border-hover transition-colors group{liveClass}" ] (cardChildren view)

    let RenderCardStatic (card: WorldCupCardData) =
        let view = viewFromCard card
        let liveClass = if view.Status = "in_progress" then " border-status-live/30 bg-status-live/5" else ""
        Elem.div [ cls $"block bg-bg-card border border-border rounded-lg p-4{liveClass}" ] (cardChildren view)

    let RenderPage (cards: Collections.Generic.List<WorldCupCardData>) =
        [
            Elem.div [ cls "max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8" ] [
                Elem.div [ cls "mb-8" ] [
                    Elem.h1 [ cls "text-2xl font-semibold mb-1 flex items-center gap-2" ] [
                        Elem.span [] [ Text.raw "⚽" ]
                        Elem.span [] [ Text.raw "FIFA World Cup" ]
                    ]
                    Elem.p [ cls "text-text-muted text-sm" ] [ Text.raw "Live-first match cards with home, draw, and away probabilities" ]
                ]
                if cards.Count = 0 then
                    Elem.div [ cls "bg-bg-card border border-border rounded-lg p-12 text-center" ] [
                        Elem.p [ cls "text-text-muted" ] [ Text.raw "No active World Cup matches are available right now." ]
                    ]
                else
                    Elem.div [ cls "grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4"; Attr.create "data-init" "@get('/sse/live/world_cup')" ] [
                        for card in cards do
                            RenderCardLink card
                    ]
            ]
        ]

    let private matchTeamColumn (abbr: string) (name: string) (score: Nullable<int>) (color: string) =
        let flag = countryFlag abbr name
        Elem.div [ cls "flex flex-col items-center gap-2 min-w-0" ] [
            if not (String.IsNullOrEmpty flag) then
                Elem.span [ cls "text-3xl leading-none"; Attr.create "aria-hidden" "true" ] [ Text.raw flag ]
            Elem.div [ cls "flex items-center gap-2" ] [
                Elem.span [ cls "inline-block w-2 h-2 rounded-full flex-shrink-0"; Attr.style $"background: {color}" ] []
                Elem.span [ cls "text-xl font-semibold text-text-primary truncate" ] [ Text.enc abbr ]
            ]
            if not (String.IsNullOrEmpty name) then
                Elem.span [ cls "text-sm text-text-muted text-center truncate" ] [ Text.enc name ]
            if score.HasValue then
                Elem.span [ cls "text-3xl font-bold tabular-nums text-text-primary" ] [ Text.raw $"{score.Value}" ]
        ]

    let private matchScores (view: WorldCupCardView) =
        Elem.div [ Attr.id $"match-scores-{view.Id}"; cls "grid grid-cols-3 gap-4 items-center text-center" ] [
            matchTeamColumn view.AwayTeamAbbreviation view.AwayTeam view.AwayScore awayColor
            Elem.div [ cls "flex flex-col items-center gap-1" ] [
                if view.Status = "scheduled" then
                    Elem.span [ cls "text-text-muted text-lg" ] [ Text.raw "VS" ]
                    Elem.span [ cls "text-text-muted text-sm" ] [ FormatHelpers.scheduledTimeNode view.ScheduledStart ]
                else
                    Elem.span [ cls "text-text-muted text-lg" ] [ Text.raw "@" ]
            ]
            matchTeamColumn view.HomeTeamAbbreviation view.HomeTeam view.HomeScore homeColor
        ]

    let private moneylineText (moneyline: Nullable<int>) =
        if moneyline.HasValue then FormatHelpers.formatMoneyline moneyline.Value else "—"

    let private oddsRow (label: string) (abbr: string) (name: string) (moneyline: Nullable<int>) (color: string) =
        Elem.div [ cls "flex items-center justify-between gap-3 py-2 border-b border-border" ] [
            Elem.div [ cls "flex items-center gap-2 min-w-0" ] [
                Elem.span [ cls "inline-block w-2 h-2 rounded-full flex-shrink-0"; Attr.style $"background: {color}" ] []
                yield! flagNode abbr name
                Elem.span [ cls "text-sm text-text-muted" ] [ Text.raw label ]
                Elem.span [ cls "font-medium text-text-primary truncate" ] [ Text.enc abbr ]
            ]
            Elem.span [ cls "font-semibold tabular-nums text-text-primary" ] [ Text.raw (moneylineText moneyline) ]
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

        Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
            Elem.h2 [ cls "text-lg font-semibold mb-1" ] [ Text.raw chartTitle ]
            Elem.p [ cls "text-xs text-text-muted mb-4" ] [ Text.raw chartDescription ]
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
                    cls "block h-64"
                ] []
            else
                Elem.div [ cls "rounded-md border border-border bg-bg-card-hover/40 px-3 py-4 text-sm text-text-muted text-center" ] [
                    Text.raw "Graph appears once live odds history has at least two updates."
                ]
        ]

    let RenderMatchPage (card: WorldCupCardData) (snapshots: Collections.Generic.List<OddsSnapshot>) =
        let view = viewFromCard card
        [
            Elem.div [ cls "max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8"; Attr.create "data-live-terminal" (if view.Status = "final" then "true" else "false") ] [
                Elem.a [ Attr.href "/world-cup"; cls "text-sm text-text-muted hover:text-text-primary" ] [ Text.raw "← All World Cup matches" ]
                Elem.div [ cls "mt-6 mb-6" ] [
                    Elem.div [ cls "flex items-center gap-2 mb-1" ] [
                        Elem.img [ Attr.src (Logos.getLeagueLogoUrl Sport.WORLD_CUP); Attr.alt "World Cup"; cls "w-5 h-5" ]
                        Elem.span [ cls "text-sm text-text-muted" ] [ Text.raw "FIFA World Cup" ]
                    ]
                    Elem.h1 [ cls "text-2xl font-semibold text-text-primary" ] [
                        Text.enc $"{view.AwayTeamAbbreviation} at {view.HomeTeamAbbreviation}"
                    ]
                    Elem.p [ cls "text-text-muted text-sm mt-1" ] [
                        Text.enc $"{view.AwayTeam} at {view.HomeTeam}"
                    ]
                ]
                Elem.div [ cls "grid grid-cols-1 md:grid-cols-3 gap-4" ] [
                    Elem.div [ cls "md:col-span-2" ] [
                        Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                            Elem.div [ cls "flex items-center justify-between mb-6" ] [
                                statusBadge "match-status-" view
                                if view.Status = "scheduled" || view.Status = "final" then
                                    Elem.span [ cls "text-sm text-text-muted" ] [ FormatHelpers.scheduledTimeNode view.ScheduledStart ]
                            ]
                            matchScores view
                            matchClock view
                        ]
                        Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6 mb-6" ] [
                            Elem.h2 [ cls "text-lg font-semibold mb-4"; Attr.id $"match-market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
                            matchProbabilityBar view
                        ]
                        probabilityChart view snapshots
                    ]
                    Elem.div [ cls "space-y-4" ] [
                        Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6" ] [
                            Elem.div [ cls "flex items-end justify-between gap-4" ] [
                                Elem.div [] [
                                    Elem.h2 [ cls "text-lg font-semibold" ] [ Text.raw "Nailbiter" ]
                                    Elem.p [ cls "text-xs text-text-muted mt-1" ] [ Text.raw (if view.IsOvertime then "Unavailable for advance odds" else "3-way closeness score") ]
                                ]
                                matchScoreBlock view
                            ]
                            if not (view.NailbiterScore.HasValue && hasThreeWayOdds view && view.Status <> "final") then
                                Elem.p [ cls "text-sm text-text-muted mt-3" ] [
                                    Text.raw (if view.IsOvertime then "Score is unavailable for two-way advance odds." else "Score unavailable until complete 3-way odds are available.")
                                ]
                        ]
                        Elem.div [ cls "bg-bg-card border border-border rounded-xl p-6" ] [
                            Elem.h2 [ cls "text-lg font-semibold mb-1" ] [ Text.raw (if view.IsOvertime then "To advance" else "Moneyline") ]
                            Elem.p [ cls "text-xs text-text-muted mb-4" ] [ Text.raw (if view.IsOvertime then "Latest live advance odds" else "Latest complete 3-way odds at page load") ]
                            oddsRow "Away" view.AwayTeamAbbreviation view.AwayTeam card.AwayMoneyline awayColor
                            if not view.IsOvertime then
                                oddsRow "Draw" "DRAW" "Draw" card.DrawMoneyline drawColor
                            oddsRow "Home" view.HomeTeamAbbreviation view.HomeTeam card.HomeMoneyline homeColor
                            if not (String.IsNullOrEmpty card.Source) || card.OddsTimestamp.HasValue then
                                Elem.div [ cls "text-xs text-text-muted mt-3" ] [
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
            Elem.div [ Attr.id $"scores-{update.GameId}"; cls "space-y-2" ] [
                teamLine view.AwayTeamAbbreviation view.AwayTeam update.AwayScore awayColor
                teamLine view.HomeTeamAbbreviation view.HomeTeam update.HomeScore homeColor
            ]
            probabilityBar view
            clock view
            scoreBlock view
            Elem.span [ cls "text-xs text-text-muted"; Attr.id $"market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
            statusBadge "status-" view
        ]
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""

    let RenderMatchFragments (update: GameUpdate) =
        let view = viewFromUpdate update
        [
            matchScores view
            Elem.h2 [ cls "text-lg font-semibold mb-4"; Attr.id $"match-market-label-{view.Id}" ] [ Text.raw (marketLabel view) ]
            matchProbabilityBar view
            matchClock view
            matchScoreBlock view
            statusBadge "match-status-" view
        ]
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""
