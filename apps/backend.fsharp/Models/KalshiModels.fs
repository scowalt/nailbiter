namespace backend.Services.Kalshi

open System
open System.Text.Json.Serialization

type KalshiCustomStrike =
    { [<JsonPropertyName("baseball_team")>] BaseballTeam: string }

type KalshiMarket =
    { [<JsonPropertyName("ticker")>] Ticker: string
      [<JsonPropertyName("event_ticker")>] EventTicker: string
      [<JsonPropertyName("yes_bid_size_fp")>] YesBidSizeFp: string
      [<JsonPropertyName("yes_ask_size_fp")>] YesAskSizeFp: string
      [<JsonPropertyName("title")>] Title: string
      [<JsonPropertyName("status")>] Status: string
      [<JsonPropertyName("yes_bid")>] YesBid: Nullable<int>
      [<JsonPropertyName("yes_ask")>] YesAsk: Nullable<int>
      [<JsonPropertyName("yes_bid_dollars")>] YesBidDollars: string
      [<JsonPropertyName("yes_ask_dollars")>] YesAskDollars: string
      [<JsonPropertyName("no_bid")>] NoBid: Nullable<int>
      [<JsonPropertyName("no_ask")>] NoAsk: Nullable<int>
      [<JsonPropertyName("volume")>] Volume: int
      [<JsonPropertyName("close_time")>] CloseTime: Nullable<DateTime>
      [<JsonPropertyName("last_price")>] LastPrice: Nullable<int>
      [<JsonPropertyName("last_price_dollars")>] LastPriceDollars: string
      [<JsonPropertyName("no_sub_title")>] NoSubTitle: string
      [<JsonPropertyName("yes_sub_title")>] YesSubTitle: string
      [<JsonPropertyName("custom_strike")>] CustomStrike: KalshiCustomStrike
      [<JsonPropertyName("rules_primary")>] RulesPrimary: string
      [<JsonPropertyName("rules_secondary")>] RulesSecondary: string
      [<JsonPropertyName("result")>] Result: string
      [<JsonPropertyName("market_type")>] MarketType: string
      [<JsonPropertyName("notional_value_dollars")>] NotionalValueDollars: string
      [<JsonPropertyName("settlement_value_dollars")>] SettlementValueDollars: string }

type KalshiMlbMilestoneDetails =
    { [<JsonPropertyName("league")>] League: string
      [<JsonPropertyName("status")>] Status: string
      [<JsonPropertyName("main_game_event_ticker")>] EventTicker: string
      [<JsonPropertyName("home_team_id")>] HomeTeamId: string
      [<JsonPropertyName("away_team_id")>] AwayTeamId: string }

type KalshiMlbMilestone =
    { [<JsonPropertyName("category")>] Category: string
      [<JsonPropertyName("type")>] Type: string
      [<JsonPropertyName("start_date")>] StartDate: Nullable<DateTimeOffset>
      [<JsonPropertyName("primary_event_tickers")>] PrimaryEventTickers: string array
      [<JsonPropertyName("details")>] Details: KalshiMlbMilestoneDetails }

type KalshiMlbMilestonesResponse =
    { [<JsonPropertyName("cursor")>] Cursor: string
      [<JsonPropertyName("milestones")>] Milestones: KalshiMlbMilestone array }

type KalshiMarketsResponse =
    { [<JsonPropertyName("markets")>] Markets: ResizeArray<KalshiMarket>
      [<JsonPropertyName("cursor")>] Cursor: string }

type KalshiSingleMarketResponse =
    { [<JsonPropertyName("market")>] Market: KalshiMarket }

type KalshiEvent =
    { [<JsonPropertyName("event_ticker")>] EventTicker: string
      [<JsonPropertyName("series_ticker")>] SeriesTicker: string
      [<JsonPropertyName("sub_title")>] SubTitle: string
      [<JsonPropertyName("markets")>] Markets: ResizeArray<KalshiMarket> }

type KalshiEventResponse =
    { [<JsonPropertyName("event")>] Event: KalshiEvent
      [<JsonPropertyName("markets")>] Markets: ResizeArray<KalshiMarket> }

[<CLIMutable>]
type KalshiGameMarket =
    { Ticker: string
      GameDate: string
      AwayTeamAbbr: string
      HomeTeamAbbr: string
      OutcomeTeamAbbr: string
      YesBid: Nullable<int>
      YesAsk: Nullable<int>
      YesBidDollars: string
      YesAskDollars: string }

[<CLIMutable>]
type KalshiMoneylineOdds =
    { GameId: string
      HomeMoneyline: int
      AwayMoneyline: int
      HomeTeamAbbr: string
      AwayTeamAbbr: string
      HomeWinProbability: float
      AwayWinProbability: float
      Source: string
      IsLive: bool }

[<CLIMutable>]
type KalshiThreeWayOdds =
    { GameId: string
      HomeMoneyline: int
      DrawMoneyline: int
      AwayMoneyline: int
      HomeTeamAbbr: string
      AwayTeamAbbr: string
      HomeWinProbability: float
      DrawWinProbability: float
      AwayWinProbability: float
      Source: string
      IsLive: bool }

type KalshiOhlcPrice =
    { [<JsonPropertyName("open")>] Open: int
      [<JsonPropertyName("high")>] High: int
      [<JsonPropertyName("low")>] Low: int
      [<JsonPropertyName("close")>] Close: int }

type KalshiCandlestickPrice =
    { [<JsonPropertyName("open")>] Open: Nullable<int>
      [<JsonPropertyName("high")>] High: Nullable<int>
      [<JsonPropertyName("low")>] Low: Nullable<int>
      [<JsonPropertyName("close")>] Close: Nullable<int>
      [<JsonPropertyName("close_dollars")>] CloseDollars: string
      [<JsonPropertyName("mean")>] Mean: Nullable<int> }

type KalshiCandlestick =
    { [<JsonPropertyName("end_period_ts")>] EndPeriodTs: int64
      [<JsonPropertyName("open_interest")>] OpenInterest: int
      [<JsonPropertyName("price")>] Price: KalshiCandlestickPrice
      [<JsonPropertyName("volume")>] Volume: int
      [<JsonPropertyName("yes_ask")>] YesAsk: KalshiOhlcPrice
      [<JsonPropertyName("yes_bid")>] YesBid: KalshiOhlcPrice }

type KalshiCandlesticksResponse =
    { [<JsonPropertyName("candlesticks")>] Candlesticks: ResizeArray<KalshiCandlestick>
      [<JsonPropertyName("ticker")>] Ticker: string }

[<CLIMutable>]
type KalshiHistoricalOdds =
    { Timestamp: DateTime
      HomeWinProbability: float
      AwayWinProbability: float
      Volume: int }
