namespace backend.Services.Kalshi

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

module internal KalshiMlbSchedule =
    let private eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York")

    let private teamId (market: KalshiMarket) =
        if isNull (box market.CustomStrike) then "" else market.CustomStrike.BaseballTeam

    let private normalize (text: string) =
        if isNull text then "" else Regex.Replace(text, @"\s+", " ").Trim()

    let private knownRules (originalStart: DateTime) (home: KalshiMarket) (away: KalshiMarket) =
        let date = originalStart.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture)
        let zone = if eastern.IsDaylightSavingTime(originalStart) then "EDT" else "EST"
        let matchesFormat scheduledFor =
            let game = $"{away.YesSubTitle} vs {home.YesSubTitle} professional baseball game originally scheduled for {scheduledFor}"
            let secondary =
                $"The following market refers to the {game}. If this game is postponed or delayed, the market will remain open and close after the rescheduled game has finished (within two days). If the game is cancelled or rescheduled to over two days away, the market will resolve to a fair price in accordance with the rules. Kalshi is not affiliated, associated, authorized, endorsed by, or in any way officially connected with the Governing League. All trademarks, logos, and brand names are the property of their respective owners."
            [ home; away ] |> List.forall (fun market ->
                not (String.IsNullOrWhiteSpace(market.YesSubTitle))
                && normalize market.RulesPrimary = $"If {market.YesSubTitle} wins the {game}, then the market resolves to Yes."
                && normalize market.RulesSecondary = secondary)
        matchesFormat $"{date} {zone}"
        || matchesFormat (originalStart.ToString("MMM d, yyyy", CultureInfo.InvariantCulture))

    let canReconcile date time (currentStart: DateTime) (now: DateTime) (home: KalshiMarket) (away: KalshiMarket) =
        let active (market: KalshiMarket) =
            market.Status = "active" && String.IsNullOrEmpty(market.Result)
            && market.MarketType = "binary" && String.IsNullOrEmpty(market.SettlementValueDollars)
            && market.CloseTime.HasValue && market.CloseTime.Value > now
            && (Decimal.TryParse(market.NotionalValueDollars, NumberStyles.Number, CultureInfo.InvariantCulture) = (true, 1m))
            && not (String.IsNullOrWhiteSpace(teamId market))
        match DateTime.TryParseExact(date + time, "yyMMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, originalStart when not (eastern.IsInvalidTime(originalStart) || eastern.IsAmbiguousTime(originalStart)) ->
            let originalUtc = TimeZoneInfo.ConvertTimeToUtc(originalStart, eastern)
            let currentEastern = TimeZoneInfo.ConvertTimeFromUtc(currentStart, eastern)
            originalStart.Date = currentEastern.Date && currentStart <> originalUtc
            && now >= currentStart && now < originalUtc.AddHours(48)
            && active home && active away && teamId home <> teamId away
            && knownRules originalStart home away
        | _ -> false

    let confirmsStart body eventTicker (currentStart: DateTime) (home: KalshiMarket) (away: KalshiMarket) =
        let response = JsonSerializer.Deserialize<KalshiMlbMilestonesResponse>(body: string)
        if isNull (box response) || not (String.IsNullOrEmpty(response.Cursor)) || isNull response.Milestones then false
        else
            match response.Milestones with
            | [| milestone |] when not (isNull (box milestone)) && not (isNull (box milestone.Details)) ->
                milestone.Category = "Sports" && milestone.Type = "baseball_game"
                && milestone.StartDate.HasValue && milestone.StartDate.Value.UtcDateTime = currentStart
                && not (isNull milestone.PrimaryEventTickers) && Array.contains eventTicker milestone.PrimaryEventTickers
                && milestone.Details.League = "MLB" && milestone.Details.Status = "inprogress"
                && milestone.Details.EventTicker = eventTicker
                && milestone.Details.HomeTeamId = teamId home && milestone.Details.AwayTeamId = teamId away
            | _ -> false
