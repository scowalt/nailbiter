module NhlQualificationSelectionTests

open System
open System.Text.Json
open Xunit
open backend.Models
open backend.Services

let private now = DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)
let private published = now.AddHours(-24.0)

let private request mode team at : PlayoffRatingPolicy.SelectionRequest =
    { Sport = "nhl"; Season = 2027; Team = team; Now = at
      Options = PlayoffRatingsOptions(NhlModelMode = mode, ApprovedSeasons = [|2027|]) }

let private kalshi team value fetched =
    TeamRating(Sport = "nhl", TeamAbbreviation = team, Season = 2027, SourceSeason = Nullable 2027,
               Source = "kalshi", PlayoffProbability = value, FetchedAt = fetched)

let private finalKalshi team value =
    let row = kalshi team (Nullable value) now
    row.PlayoffProbabilityIsFinal <- true
    let ticker = $"KXNHLPLAYOFF-{2027 % 100:D2}"
    let evidence =
        PlayoffQuoteEvidence(Version = 1, ValidationPolicy = "kalshi-qualification-v1",
                             Provider = PlayoffRatingPolicy.kalshiSource, Sport = "nhl", Team = team,
                             Season = 2027, EventId = ticker, MarketId = ticker + "-" + team,
                             ContractVerified = true, State = "final", ReasonCode = "accepted",
                             ObservedAt = Nullable now, EvidenceAt = Nullable now, ResolutionAt = Nullable now,
                             YesPayout = Nullable(decimal value), NoPayout = Nullable(1M - decimal value),
                             ResolutionReference = "synthetic-test-final")
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)
    row

let private model team value pub fetched =
    let evidence =
        PlayoffModelEvidence(Version = 2, EvidenceKind = "model_projection",
                             ValidationPolicy = PlayoffRatingPolicy.playoffStatusValidationPolicy,
                             Provider = PlayoffRatingPolicy.playoffStatusSource, Sport = "nhl", Team = team,
                             Season = 2027, SeasonVerified = true, TeamVerified = true,
                             State = "estimate", ReasonCode = "accepted",
                             PublishedAt = Nullable pub, FetchedAt = Nullable fetched,
                             AcceptedTeamCount = 32, TotalTeamCount = 32)
    TeamRating(Sport = "nhl", TeamAbbreviation = team, Season = 2027, SourceSeason = Nullable 2027,
               Source = PlayoffRatingPolicy.playoffStatusSource, PlayoffProbability = value,
               FetchedAt = fetched, PlayoffEvidenceJson = JsonSerializer.Serialize(evidence))

let private select mode rows = PlayoffRatingPolicy.select (request mode "BOS" now) rows

let private precisionBucket = now.AddMinutes(-1.0).AddTicks(8295030L)

let private precisionQuote remainder =
    let row = finalKalshi "BOS" 1.0
    row.PlayoffProbabilityIsFinal <- false
    row.PlayoffProbability <- Nullable 0.15
    row.FetchedAt <- precisionBucket
    let e = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    e.State <- "estimate"
    e.ObservedAt <- Nullable(precisionBucket.AddTicks(int64 remainder))
    e.EvidenceAt <- e.ObservedAt
    e.Bid <- Nullable 0.14M
    e.Ask <- Nullable 0.16M
    e.BidSize <- Nullable 20M
    e.AskSize <- Nullable 5M
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    row

[<Fact>]
let ``Kalshi estimate wins over a newer valid model`` () =
    let k = kalshi "BOS" (Nullable 0.60) now
    let m = model "BOS" (Nullable 0.51) published (now.AddMinutes(5.0))
    let selected = select "fallback" [m; k]
    Assert.Equal(Some "kalshi", selected.Source)
    Assert.Equal(Some 0.60, selected.Probability)
    Assert.Equal(Some(now.AddHours(1.0)), selected.ExpiresAt)

[<Fact>]
let ``Eligible PlayoffStatus model fills only a genuine latest Kalshi gap`` () =
    let oldKalshi = kalshi "BOS" (Nullable 0.80) (now.AddMinutes(-10.0))
    let latestKalshi = kalshi "BOS" (Nullable()) now
    let m = model "BOS" (Nullable 0.51) published now
    let selected = select "fallback" [oldKalshi; latestKalshi; m]
    Assert.Equal(Some PlayoffRatingPolicy.playoffStatusSource, selected.Source)
    Assert.Equal(Some 0.51, selected.Probability)
    Assert.Equal("missing_probability", selected.Providers[0].Reason)
    Assert.Equal(Some(published.AddHours(36.0)), selected.ExpiresAt)

[<Fact>]
let ``Newer invalid model rows do not resurrect older same-source model estimates`` () =
    let oldModel = model "BOS" (Nullable 0.51) published (now.AddMinutes(-5.0))
    let latestModel = model "BOS" (Nullable()) now now
    let k = kalshi "BOS" (Nullable()) now
    let selected = select "fallback" [oldModel; latestModel; k]
    Assert.True(selected.Probability.IsNone)
    Assert.True(selected.Source.IsNone)
    Assert.Equal("missing_probability", selected.Providers[1].Reason)

[<Fact>]
let ``Verified Kalshi finals beat contrary model estimates and model endpoints are never finals`` () =
    let yes = finalKalshi "BOS" 1.0
    let m = model "BOS" (Nullable 0.0) published now
    let selected = select "fallback" [m; yes]
    Assert.Equal(Some "kalshi", selected.Source)
    Assert.Equal(Some 1.0, selected.Probability)
    Assert.True(selected.IsFinal)

    let onlyModel = select "fallback" [m]
    Assert.Equal(Some 0.0, onlyModel.Probability)
    Assert.False(onlyModel.IsFinal)

[<Theory>]
[<InlineData(35.999, true)>]
[<InlineData(36.0, false)>]
[<InlineData(37.0, false)>]
let ``Model expiry uses publication time strictly below thirty six hours`` ageHours accepted =
    let at = now
    let pub = at.AddHours(-ageHours)
    let m = model "BOS" (Nullable 0.51) pub at
    let selected = PlayoffRatingPolicy.select (request "fallback" "BOS" at) [m]
    Assert.Equal(accepted, selected.Probability.IsSome)

[<Fact>]
let ``Fetch time and repeated downloads do not renew model age`` () =
    let oldPublication = now.AddHours(-35.0)
    let firstFetch = now.AddHours(-10.0)
    let repeatedFetch = now
    let m = model "BOS" (Nullable 0.51) oldPublication repeatedFetch
    let accepted = PlayoffRatingPolicy.select (request "fallback" "BOS" now) [m]
    Assert.Equal(Some 0.51, accepted.Probability)
    let expired = PlayoffRatingPolicy.select (request "fallback" "BOS" (oldPublication.AddHours(36.0))) [m]
    Assert.True(expired.Probability.IsNone)

[<Theory>]
[<InlineData("future_publication")>]
[<InlineData("future_observation")>]
[<InlineData("wrong_provider")>]
[<InlineData("wrong_season")>]
[<InlineData("wrong_team")>]
[<InlineData("unverified")>]
[<InlineData("claimed_final")>]
let ``Invalid model evidence is unavailable and does not backfill scalar values`` invalid =
    let m = model "BOS" (Nullable 0.51) published now
    let e = PlayoffModelEvidence.Parse(m.PlayoffEvidenceJson)
    match invalid with
    | "future_publication" -> e.PublishedAt <- Nullable(now.AddSeconds(1.0))
    | "future_observation" -> e.FetchedAt <- Nullable(now.AddSeconds(1.0))
    | "wrong_provider" -> e.Provider <- "kalshi"
    | "wrong_season" -> e.Season <- 2026
    | "wrong_team" -> e.Team <- "TOR"
    | "unverified" -> e.TeamVerified <- false
    | "claimed_final" -> m.PlayoffProbabilityIsFinal <- true
    | _ -> ()
    m.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    Assert.True((select "fallback" [m]).Probability.IsNone, invalid)

[<Fact>]
let ``Legacy quote-shaped PlayoffStatus rows are not eligible model evidence`` () =
    let legacy = model "BOS" (Nullable 0.51) published now
    let quote =
        PlayoffQuoteEvidence(Version = 1, ValidationPolicy = "playoffstatus-nhl-qualification-v1",
                             Provider = PlayoffRatingPolicy.playoffStatusSource, Sport = "nhl", Team = "BOS",
                             Season = 2027, ContractVerified = true, State = "estimate", ReasonCode = "accepted",
                             ObservedAt = Nullable now, EvidenceAt = Nullable published)
    legacy.PlayoffEvidenceJson <- JsonSerializer.Serialize(quote)
    let selected = select "fallback" [legacy]
    Assert.True(selected.Probability.IsNone)
    Assert.Equal("unverified_model_evidence", selected.Providers[1].Reason)

[<Fact>]
let ``Default off and shadow modes keep runtime Kalshi only while other sports remain unchanged`` () =
    let m = model "BOS" (Nullable 0.51) published now
    Assert.True((select "off" [m]).Probability.IsNone)
    Assert.True((select "shadow" [m]).Probability.IsNone)
    Assert.True((PlayoffRatingPolicy.select { request "fallback" "BOS" now with Sport = "nba" } [m]).Probability.IsNone)

[<Fact>]
let ``Selection isolates season and recognizes the NHL Utah aliases`` () =
    let m = model "UTAH" (Nullable 0.44) published now
    m.TeamAbbreviation <- "UTA"
    let selected = PlayoffRatingPolicy.select (request "fallback" "UTAH" now) [m]
    Assert.Equal(Some 0.44, selected.Probability)
    let wrongTeam = PlayoffRatingPolicy.select (request "fallback" "BOS" now) [m]
    Assert.True(wrongTeam.Probability.IsNone)
    m.Season <- 2026
    let wrongSeason = PlayoffRatingPolicy.select (request "fallback" "UTAH" now) [m]
    Assert.True(wrongSeason.Probability.IsNone)

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
[<InlineData(4)>]
[<InlineData(5)>]
[<InlineData(6)>]
[<InlineData(7)>]
[<InlineData(8)>]
[<InlineData(9)>]
let ``Model JSON fetch precision survives scalar microseconds without renewing publication age`` remainder =
    let scalarFetch = now.AddMinutes(-1.0)
    let jsonFetch = scalarFetch.AddTicks(int64 remainder)
    let m = model "BOS" (Nullable 0.51) published jsonFetch
    m.FetchedAt <- scalarFetch
    let selected = select "fallback" [m]
    Assert.Equal(Some 0.51, selected.Probability)
    Assert.Equal("accepted", selected.Providers[1].Reason)
    Assert.Equal(Some published, selected.EvidenceAt)
    Assert.Equal(Some(published.AddHours(36.0)), selected.ExpiresAt)
    Assert.True((PlayoffRatingPolicy.select (request "fallback" "BOS" (published.AddHours(36.0))) [m]).Probability.IsNone)
    Assert.True((PlayoffRatingPolicy.select { request "off" "BOS" now with Options = PlayoffRatingsOptions() } [m]).Probability.IsNone)

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
[<InlineData(4)>]
[<InlineData(5)>]
[<InlineData(6)>]
[<InlineData(7)>]
[<InlineData(8)>]
[<InlineData(9)>]
let ``Qualification JSON observation and evidence survive microsecond truncation with original expiry`` remainder =
    let row = precisionQuote remainder
    let evidenceAt = precisionBucket.AddTicks(int64 remainder)
    let selected = select "off" [row]
    Assert.Equal(Some 0.15, selected.Probability)
    Assert.Equal("accepted", selected.Reason)
    Assert.Equal(Some evidenceAt, selected.EvidenceAt)
    Assert.Equal(Some(evidenceAt.AddHours(1.0)), selected.ExpiresAt)
    let beforeExpiry = PlayoffRatingPolicy.select (request "off" "BOS" (evidenceAt.AddHours(1.0).AddTicks(-1L))) [row]
    Assert.Equal(Some 0.15, beforeExpiry.Probability)
    let expired = PlayoffRatingPolicy.select (request "off" "BOS" (evidenceAt.AddHours(1.0))) [row]
    Assert.True(expired.Probability.IsNone)
    Assert.Equal("expired", expired.Reason)

    let e = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    e.ObservedAt <- Nullable(precisionBucket.AddTicks(-10L + int64 remainder))
    e.EvidenceAt <- Nullable(precisionBucket.AddTicks(-20L + int64 remainder))
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    let earlier = select "off" [row]
    Assert.Equal(Some 0.15, earlier.Probability)
    Assert.Equal(Some(e.EvidenceAt.Value.AddHours(1.0)), earlier.ExpiresAt)

    e.ObservedAt <- Nullable(precisionBucket.AddTicks(9L))
    e.EvidenceAt <- Nullable evidenceAt
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    Assert.Equal(Some 0.15, (select "off" [row]).Probability)

    for probability in [0.0; 1.0] do
        let final = finalKalshi "BOS" probability
        final.FetchedAt <- precisionBucket
        let finalEvidence = PlayoffQuoteEvidence.Parse(final.PlayoffEvidenceJson)
        finalEvidence.ObservedAt <- Nullable evidenceAt
        finalEvidence.EvidenceAt <- Nullable evidenceAt
        finalEvidence.ResolutionAt <- Nullable evidenceAt
        final.PlayoffEvidenceJson <- JsonSerializer.Serialize(finalEvidence)
        let outcome = PlayoffRatingPolicy.select (request "off" "BOS" (now.AddYears(1))) [final]
        Assert.Equal(Some probability, outcome.Probability)
        Assert.True(outcome.IsFinal)
        Assert.True(outcome.ExpiresAt.IsNone)

[<Theory>]
[<InlineData(0.05, true)>]
[<InlineData(0.06, false)>]
let ``An NHL market with no buyers selects half its ask and keeps the ask as the bound`` (ask: float) accepted =
    let row = precisionQuote 0
    row.PlayoffProbability <- Nullable(ask / 2.0)
    let e = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    e.Bid <- Nullable 0M
    e.BidSize <- Nullable 0M
    e.BidExplicitlyEmpty <- true
    e.Ask <- Nullable(decimal ask)
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    let selected = select "off" [row]
    if accepted then
        Assert.Equal(Some(ask / 2.0), selected.Probability)
        Assert.Equal(Some ask, selected.UpperBound)
    else
        Assert.True(selected.Probability.IsNone)
        Assert.True(selected.UpperBound.IsNone)
        Assert.Equal("insufficient_depth", (selected.Providers |> List.find (fun p -> p.Source = "kalshi")).Reason)

[<Theory>]
[<InlineData("later_microsecond", "future_observation")>]
[<InlineData("evidence_after_observation_tick", "future_observation")>]
[<InlineData("observation_after_now_tick", "future_observation")>]
[<InlineData("scalar_after_now_tick", "future_observation")>]
[<InlineData("wrong_season", "identity_mismatch")>]
[<InlineData("wrong_team", "identity_mismatch")>]
[<InlineData("unverified_contract", "identity_mismatch")>]
[<InlineData("unsupported_contract", "unsupported_contract")>]
[<InlineData("unverified_scalar_season", "unverified_season")>]
[<InlineData("missing_probability", "inconsistent_projection")>]
[<InlineData("inconsistent_probability", "inconsistent_projection")>]
[<InlineData("wide_spread", "wide_spread")>]
[<InlineData("insufficient_depth", "insufficient_depth")>]
[<InlineData("blocked_resolution", "disputed_resolution")>]
[<InlineData("unverified_final", "unverified_final")>]
[<InlineData("resolution_after_observation_tick", "unverified_final")>]
let ``Precision recovery preserves qualification rejection safeguards and strict JSON clock ordering`` invalid reason =
    let row = precisionQuote 4
    let e = PlayoffQuoteEvidence.Parse(row.PlayoffEvidenceJson)
    let mutable at = now
    match invalid with
    | "later_microsecond" ->
        e.ObservedAt <- Nullable(precisionBucket.AddTicks(10L))
        e.EvidenceAt <- e.ObservedAt
    | "evidence_after_observation_tick" -> e.EvidenceAt <- Nullable(e.ObservedAt.Value.AddTicks(1L))
    | "observation_after_now_tick" -> at <- e.ObservedAt.Value.AddTicks(-1L)
    | "scalar_after_now_tick" -> row.FetchedAt <- now.AddTicks(1L)
    | "wrong_season" -> e.Season <- 2026
    | "wrong_team" -> e.Team <- "TOR"
    | "unverified_contract" -> e.ContractVerified <- false
    | "unsupported_contract" -> e.MarketId <- "KXNHLPLAYOFF-27-TOR"
    | "unverified_scalar_season" -> row.SourceSeason <- Nullable()
    | "missing_probability" -> row.PlayoffProbability <- Nullable()
    | "inconsistent_probability" -> row.PlayoffProbability <- Nullable 0.16
    | "wide_spread" -> e.Ask <- Nullable 0.98M
    | "insufficient_depth" -> e.BidSize <- Nullable 0M
    | "blocked_resolution" ->
        e.State <- "resolution_blocked"
        e.ResolutionBlocked <- true
        e.ReasonCode <- "disputed_resolution"
    | "unverified_final" -> e.State <- "final"; row.PlayoffProbabilityIsFinal <- true
    | "resolution_after_observation_tick" ->
        e.State <- "final"
        row.PlayoffProbabilityIsFinal <- true
        row.PlayoffProbability <- Nullable 1.0
        e.ResolutionAt <- Nullable(e.ObservedAt.Value.AddTicks(1L))
    | _ -> failwith invalid
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    let selected = PlayoffRatingPolicy.select (request "off" "BOS" at) [row]
    Assert.True(selected.Probability.IsNone, invalid)
    Assert.Equal(reason, selected.Reason)

    if invalid = "blocked_resolution" then
        let fallback = select "fallback" [row; model "BOS" (Nullable 0.51) published now]
        Assert.True(fallback.Probability.IsNone)
        Assert.Equal("resolution_blocked", fallback.Reason)

[<Theory>]
[<InlineData("later_microsecond")>]
[<InlineData("publication_after_fetch_tick")>]
[<InlineData("fetch_after_now_tick")>]
[<InlineData("scalar_after_now_tick")>]
let ``Model persistence precision never relaxes later buckets or full precision source clocks`` invalid =
    let m = model "BOS" (Nullable 0.51) published precisionBucket
    let e = PlayoffModelEvidence.Parse(m.PlayoffEvidenceJson)
    let mutable at = now
    match invalid with
    | "later_microsecond" -> e.FetchedAt <- Nullable(precisionBucket.AddTicks(10L))
    | "publication_after_fetch_tick" -> e.PublishedAt <- Nullable(precisionBucket.AddTicks(1L))
    | "fetch_after_now_tick" ->
        e.FetchedAt <- Nullable(precisionBucket.AddTicks(4L))
        at <- precisionBucket.AddTicks(3L)
    | "scalar_after_now_tick" -> m.FetchedAt <- now.AddTicks(1L)
    | _ -> failwith invalid
    m.PlayoffEvidenceJson <- JsonSerializer.Serialize(e)
    let selected = PlayoffRatingPolicy.select (request "fallback" "BOS" at) [m]
    Assert.True(selected.Probability.IsNone, invalid)
    Assert.Equal("future_observation", selected.Providers[1].Reason)

[<Fact>]
let ``Earlier model fetch remains eligible and publication expires at the original tick`` () =
    let m = model "BOS" (Nullable 0.51) published (precisionBucket.AddTicks(-1L))
    m.FetchedAt <- precisionBucket
    Assert.Equal(Some 0.51, (select "fallback" [m]).Probability)
    let expires = published.AddHours(36.0)
    Assert.Equal(Some 0.51, (PlayoffRatingPolicy.select (request "fallback" "BOS" (expires.AddTicks(-1L))) [m]).Probability)
    let expired = PlayoffRatingPolicy.select (request "fallback" "BOS" expires) [m]
    Assert.True(expired.Probability.IsNone)
    Assert.Equal("expired", expired.Providers[1].Reason)

[<Fact>]
let ``CFB no-market assumptions remain separate from NHL model fallback`` () =
    let assumption = TeamRating(Sport = "cfb", TeamAbbreviation = "ARMY", Season = 2026, SourceSeason = Nullable 2026,
                                Source = PlayoffRatingPolicy.collegeNoMarketSource,
                                PlayoffProbability = Nullable PlayoffRatingPolicy.collegeNoMarketProbability,
                                FetchedAt = now)
    let selected =
        PlayoffRatingPolicy.select
            { Sport = "cfb"; Season = 2026; Team = "ARMY"; Now = now; Options = PlayoffRatingsOptions(NhlModelMode = "fallback", ApprovedSeasons = [|2027|]) }
            [assumption]
    Assert.Equal(Some PlayoffRatingPolicy.collegeNoMarketProbability, selected.Probability)
    Assert.True(selected.IsAssumed)
