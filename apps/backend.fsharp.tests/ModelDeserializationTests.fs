module ModelDeserializationTests

open System
open System.Text.Json
open Xunit
open backend.Services.Espn
open backend.Services.Kalshi
open backend.Services.OpenF1
open Backend.FSharp


[<Fact>]
let ``EspnOddsParsing parses complete World Cup 3-way odds`` () =
    let json = """
    [
      {
        "provider": { "id": "100", "name": "DraftKings" },
        "overUnder": 2.5,
        "drawOdds": { "moneyLine": 275 },
        "moneyline": {
          "home": { "close": { "odds": "-130" } },
          "away": { "close": { "odds": "+390" } },
          "draw": { "close": { "odds": "+275" } }
        }
      }
    ]
    """

    let odds = JsonSerializer.Deserialize<ResizeArray<EspnOdds>>(json)
    let parsed = EspnOddsParsing.tryFindCompleteThreeWayOdds odds

    Assert.True(parsed.IsSome)
    Assert.Equal(-130, parsed.Value.HomeMoneyline)
    Assert.Equal(275, parsed.Value.DrawMoneyline)
    Assert.Equal(390, parsed.Value.AwayMoneyline)
    Assert.Equal("DraftKings", parsed.Value.ProviderName)
    Assert.Equal(2.5, parsed.Value.OverUnder.Value)

[<Fact>]
let ``EspnOddsParsing uses drawOdds fallback when moneyline draw is absent`` () =
    let json = """
    [
      {
        "provider": { "id": "100", "name": "DraftKings" },
        "drawOdds": { "moneyLine": 300 },
        "moneyline": {
          "home": { "close": { "odds": "-165" } },
          "away": { "close": { "odds": "+475" } }
        }
      }
    ]
    """

    let odds = JsonSerializer.Deserialize<ResizeArray<EspnOdds>>(json)
    let parsed = EspnOddsParsing.tryFindCompleteThreeWayOdds odds

    Assert.True(parsed.IsSome)
    Assert.Equal(-165, parsed.Value.HomeMoneyline)
    Assert.Equal(300, parsed.Value.DrawMoneyline)
    Assert.Equal(475, parsed.Value.AwayMoneyline)

[<Fact>]
let ``EspnOddsParsing rejects incomplete World Cup odds`` () =
    let json = """
    [
      {
        "moneyline": {
          "home": { "close": { "odds": "-130" } },
          "away": { "close": { "odds": "+390" } }
        }
      }
    ]
    """

    let odds = JsonSerializer.Deserialize<ResizeArray<EspnOdds>>(json)
    Assert.True((EspnOddsParsing.tryFindCompleteThreeWayOdds odds).IsNone)


[<Fact>]
let ``EspnScoreboardResponse deserializes with nested events`` () =
    let json = """
    {
        "events": [
            {
                "id": "401547417",
                "name": "Green Bay Packers at Chicago Bears",
                "shortName": "GB @ CHI",
                "date": "2026-01-10T18:00Z",
                "status": {
                    "clock": 0.0,
                    "displayClock": "0:00",
                    "period": 0,
                    "type": {
                        "id": "1",
                        "name": "STATUS_SCHEDULED",
                        "state": "pre",
                        "completed": false,
                        "description": "Scheduled"
                    }
                },
                "competitions": [
                    {
                        "id": "401547417",
                        "competitors": [
                            {
                                "id": "3",
                                "homeAway": "home",
                                "score": "0",
                                "team": {
                                    "id": "3",
                                    "displayName": "Chicago Bears",
                                    "abbreviation": "CHI"
                                }
                            },
                            {
                                "id": "9",
                                "homeAway": "away",
                                "score": "0",
                                "team": {
                                    "id": "9",
                                    "displayName": "Green Bay Packers",
                                    "abbreviation": "GB"
                                }
                            }
                        ],
                        "odds": [
                            {
                                "provider": { "id": "45", "name": "Caesars Sportsbook" },
                                "spread": -3.5,
                                "overUnder": 44.5,
                                "moneyline": {
                                    "home": { "close": { "odds": "-160" } },
                                    "away": { "close": { "odds": "+140" } }
                                }
                            }
                        ]
                    }
                ],
                "week": { "number": 18 },
                "season": { "year": 2025, "type": 2, "slug": "regular-season" }
            }
        ],
        "week": { "number": 18 },
        "season": { "type": 2, "year": 2025 },
        "leagues": [
            {
                "id": "28",
                "name": "National Football League",
                "abbreviation": "NFL",
                "season": {
                    "year": 2025,
                    "type": { "id": "2", "type": 2, "name": "Regular Season", "abbreviation": "reg" }
                },
                "calendar": []
            }
        ]
    }
    """

    let result = JsonSerializer.Deserialize<EspnScoreboardResponse>(json)

    Assert.Single(result.Events) |> ignore
    let event = result.Events[0]
    Assert.Equal("401547417", event.Id)
    Assert.Equal("Green Bay Packers at Chicago Bears", event.Name)
    Assert.Equal(18, event.Week.Number)

    let competition = event.Competitions[0]
    Assert.Equal(2, competition.Competitors.Count)

    let home = competition.Competitors |> Seq.find (fun c -> c.HomeAway = "home")
    Assert.Equal("CHI", home.Team.Abbreviation)

    let away = competition.Competitors |> Seq.find (fun c -> c.HomeAway = "away")
    Assert.Equal("GB", away.Team.Abbreviation)

    Assert.Equal("pre", event.Status.Type.State)
    Assert.False(event.Status.Type.Completed)

    let odds = competition.Odds[0]
    Assert.Equal("Caesars Sportsbook", odds.Provider.Name)
    Assert.Equal(-3.5, odds.Spread.Value)
    Assert.Equal(44.5, odds.OverUnder.Value)

[<Fact>]
let ``EspnScoreboardResponse deserializes with empty events`` () =
    let json = """
    {
        "events": [],
        "week": { "number": 1 },
        "season": { "type": 1, "year": 2025 },
        "leagues": []
    }
    """

    let result = JsonSerializer.Deserialize<EspnScoreboardResponse>(json)
    Assert.Empty(result.Events)


[<Fact>]
let ``KalshiMarketsResponse deserializes with market data`` () =
    let json = """
    {
        "markets": [
            {
                "ticker": "KXNFLGAME-26JAN10GBCHI-CHI",
                "title": "Will the Chicago Bears beat the Green Bay Packers?",
                "status": "open",
                "yes_bid": 62,
                "yes_ask": 64,
                "yes_bid_dollars": "0.62",
                "yes_ask_dollars": "0.64",
                "no_bid": 36,
                "no_ask": 38,
                "volume": 15420,
                "close_time": "2026-01-11T02:00:00Z",
                "last_price": 63,
                "last_price_dollars": "0.63",
                "no_sub_title": "Green Bay Packers",
                "result": null
            }
        ],
        "cursor": "abc123"
    }
    """

    let result = JsonSerializer.Deserialize<KalshiMarketsResponse>(json)

    Assert.Single(result.Markets) |> ignore
    let market = result.Markets[0]
    Assert.Equal("KXNFLGAME-26JAN10GBCHI-CHI", market.Ticker)
    Assert.Equal("open", market.Status)
    Assert.Equal(62, market.YesBid.Value)
    Assert.Equal(64, market.YesAsk.Value)
    Assert.Equal("0.62", market.YesBidDollars)
    Assert.Equal(15420, market.Volume)
    Assert.Null(market.Result)
    Assert.Equal("abc123", result.Cursor)

[<Fact>]
let ``KalshiMarketsResponse deserializes with null nullable fields`` () =
    let json = """
    {
        "markets": [
            {
                "ticker": "KXNBAGAME-25DEC27NYKATL-NYK",
                "title": "Will the New York Knicks beat the Atlanta Hawks?",
                "status": "closed",
                "yes_bid": null,
                "yes_ask": null,
                "yes_bid_dollars": null,
                "yes_ask_dollars": null,
                "no_bid": null,
                "no_ask": null,
                "volume": 0,
                "close_time": null,
                "last_price": null,
                "last_price_dollars": null,
                "no_sub_title": "Atlanta Hawks",
                "result": "yes"
            }
        ],
        "cursor": ""
    }
    """

    let result = JsonSerializer.Deserialize<KalshiMarketsResponse>(json)

    let market = result.Markets[0]
    Assert.False(market.YesBid.HasValue)
    Assert.False(market.YesAsk.HasValue)
    Assert.False(market.LastPrice.HasValue)
    Assert.False(market.CloseTime.HasValue)
    Assert.Equal("yes", market.Result)

[<Fact>]
let ``KalshiCandlesticksResponse deserializes with candlestick data`` () =
    let json = """
    {
        "candlesticks": [
            {
                "end_period_ts": 1704067200,
                "open_interest": 500,
                "price": {
                    "open": 55,
                    "high": 60,
                    "low": 50,
                    "close": 58,
                    "mean": 56
                },
                "volume": 120,
                "yes_ask": { "open": 60, "high": 65, "low": 55, "close": 62 },
                "yes_bid": { "open": 54, "high": 59, "low": 49, "close": 57 }
            }
        ],
        "ticker": "KXNHLGAME-25DEC27BOSTBR-BOS"
    }
    """

    let result = JsonSerializer.Deserialize<KalshiCandlesticksResponse>(json)

    Assert.Single(result.Candlesticks) |> ignore
    Assert.Equal("KXNHLGAME-25DEC27BOSTBR-BOS", result.Ticker)

    let candle = result.Candlesticks[0]
    Assert.Equal(1704067200L, candle.EndPeriodTs)
    Assert.Equal(500, candle.OpenInterest)
    Assert.Equal(55, candle.Price.Open.Value)
    Assert.Equal(60, candle.Price.High.Value)
    Assert.Equal(58, candle.Price.Close.Value)
    Assert.Equal(56, candle.Price.Mean.Value)
    Assert.Equal(120, candle.Volume)
    Assert.Equal(60, candle.YesAsk.Open)
    Assert.Equal(54, candle.YesBid.Open)


[<Fact>]
let ``OpenF1Session array deserializes with jsonOptions`` () =
    let json = """
    [
        {
            "session_key": 9500,
            "session_type": "Race",
            "session_name": "Race",
            "date_start": "2025-03-16T05:00:00+00:00",
            "date_end": "2025-03-16T07:00:00+00:00",
            "meeting_key": 1240,
            "circuit_key": 1,
            "circuit_short_name": "Melbourne",
            "country_code": "AUS",
            "country_name": "Australia",
            "location": "Melbourne",
            "year": 2025
        }
    ]
    """

    let result = JsonSerializer.Deserialize<OpenF1Session[]>(json, HttpHelpers.jsonOptions)

    Assert.Single(result) |> ignore
    let session = result[0]
    Assert.Equal(9500, session.SessionKey)
    Assert.Equal("Race", session.SessionType)
    Assert.Equal("Race", session.SessionName)
    Assert.Equal(1240, session.MeetingKey)
    Assert.Equal("AUS", session.CountryCode)
    Assert.Equal(2025, session.Year)
    Assert.True(session.DateStart.HasValue)
    Assert.Equal(1, session.CircuitKey.Value)

[<Fact>]
let ``OpenF1Session deserializes with null optional fields`` () =
    let json = """
    [
        {
            "session_key": 9501,
            "session_type": "Practice",
            "session_name": "Practice 1",
            "date_start": null,
            "date_end": null,
            "meeting_key": 1240,
            "circuit_key": null,
            "circuit_short_name": null,
            "country_code": "AUS",
            "country_name": "Australia",
            "location": "Melbourne",
            "year": 2025
        }
    ]
    """

    let result = JsonSerializer.Deserialize<OpenF1Session[]>(json, HttpHelpers.jsonOptions)

    let session = result[0]
    Assert.Equal(9501, session.SessionKey)
    Assert.False(session.DateStart.HasValue)
    Assert.False(session.CircuitKey.HasValue)


[<Fact>]
let ``Malformed JSON throws JsonException for EspnScoreboardResponse`` () =
    let json = """{ "events": [{ invalid json }] }"""
    Assert.Throws<JsonException>(fun () ->
        JsonSerializer.Deserialize<EspnScoreboardResponse>(json) |> ignore
    ) |> ignore

[<Fact>]
let ``Malformed JSON throws JsonException for KalshiMarketsResponse`` () =
    let json = """{"""
    Assert.Throws<JsonException>(fun () ->
        JsonSerializer.Deserialize<KalshiMarketsResponse>(json) |> ignore
    ) |> ignore
