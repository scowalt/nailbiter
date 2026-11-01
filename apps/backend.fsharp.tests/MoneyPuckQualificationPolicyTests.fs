module MoneyPuckQualificationPolicyTests

open System
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open backend.Models
open backend.Services

let private now = DateTime(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc)
let private published = now.AddHours(-24.0)
let private options () =
    JsonSerializer.Deserialize<PlayoffRatingsOptions>("""{"MoneyPuckSelectionEnabled":true,"MoneyPuckApprovedSeasons":[2027]}""")
let private request () : PlayoffRatingPolicy.SelectionRequest =
    { Sport = "nhl"; Season = 2027; Team = "NSH"; Now = now; Options = options () }
let private model () =
    TeamRating(Sport = "nhl", Season = 2027, SourceSeason = Nullable 2027, TeamAbbreviation = "NSH",
               Source = "moneypuck_qualification", PlayoffProbability = Nullable 0.2518, FetchedAt = now,
               PlayoffEvidenceJson = $$$"""{"Version":3,"EvidenceKind":"model_projection","Provider":"moneypuck_qualification","ProviderVerified":true,"ValidationPolicy":"moneypuck-nhl-qualification-model-v1","Sport":"nhl","Team":"NSH","TeamVerified":true,"Season":2027,"SeasonVerified":true,"State":"estimate","ReasonCode":"accepted","PublishedAt":"{{{published:O}}}","FetchedAt":"{{{now:O}}}","RefreshId":"synthetic-policy-test","AcceptedTeamCount":32,"TotalTeamCount":32,"MoneyPuckSeasonProof":{"PageSeasonLabel":"2026–27","PageSeasonStartYear":2026,"PageSeasonEndYear":2027,"PageReference":"https://moneypuck.com/predictions.htm","PageObservedAt":"{{{now:O}}}","FirstSimulatedId":2099020002,"VerifiedGames":[{"SimulatedId":2099020002,"GameId":2099020002,"Season":20262027,"Reference":"https://api-web.nhle.com/v1/gamecenter/2099020002/landing","ObservedAt":"{{{now:O}}}"}]}}""")
let private quote team bid ask fetched =
    let ticker = "KXNHLPLAYOFF-27"
    let evidence =
        PlayoffQuoteEvidence(Version = 1, ValidationPolicy = "kalshi-qualification-v1", Provider = "kalshi",
                             Sport = "nhl", Team = team, Season = 2027, EventId = ticker, MarketId = ticker + "-" + team,
                             ContractVerified = true, State = "estimate", ReasonCode = "accepted",
                             Bid = Nullable bid, Ask = Nullable ask, BidSize = Nullable 20M, AskSize = Nullable 20M,
                             EvidenceAt = Nullable fetched, ObservedAt = Nullable fetched)
    TeamRating(Sport = "nhl", Season = 2027, SourceSeason = Nullable 2027, TeamAbbreviation = team,
               Source = "kalshi", FetchedAt = fetched, PlayoffProbability = Nullable(float ((bid + ask) / 2M)),
               PlayoffEvidenceJson = JsonSerializer.Serialize(evidence))
let private select rows = PlayoffRatingPolicy.select (request ()) rows
let private changeEvidence (row: TeamRating) change =
    let evidence = PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)
    change evidence
    row.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)

[<Fact>]
let ``Explicit MoneyPuck qualification selection fills a missing Kalshi estimate`` () =
    let selected = select [model ()]
    Assert.Equal(Some "moneypuck_qualification", selected.Source)
    Assert.Equal(Some 0.2518, selected.Probability)
    Assert.Equal(Some published, selected.EvidenceAt)
    Assert.Equal(Some(published.AddHours(36.0)), selected.ExpiresAt)
    Assert.False(selected.IsFinal)

[<Theory>]
[<InlineData("off")>]
[<InlineData("no_allowlist")>]
[<InlineData("unsupported_season")>]
[<InlineData("other_sport")>]
[<InlineData("refresh_only")>]
let ``MoneyPuck controls are independent default off and limited to explicitly approved NHL season 2027`` case =
    let opts = options ()
    let mutable req = request ()
    match case with
    | "off" -> opts.MoneyPuckSelectionEnabled <- false
    | "no_allowlist" -> opts.MoneyPuckApprovedSeasons <- [||]
    | "unsupported_season" -> opts.MoneyPuckApprovedSeasons <- [|2028|]; req <- { req with Season = 2028 }
    | "other_sport" -> req <- { req with Sport = "nba" }
    | "refresh_only" -> opts.MoneyPuckSelectionEnabled <- false; opts.MoneyPuckRefreshEnabled <- true
    | _ -> failwith case
    let row = model ()
    row.Season <- req.Season
    row.SourceSeason <- Nullable req.Season
    row.Sport <- req.Sport
    Assert.False(opts.MoneyPuckSelectionEnabledFor(req.Sport, req.Season))
    Assert.True((PlayoffRatingPolicy.select { req with Options = opts } [row]).Probability.IsNone)

[<Fact>]
let ``MoneyPuck refresh and selection never enable rejected PlayoffStatus controls`` () =
    let opts = options ()
    opts.MoneyPuckRefreshEnabled <- true
    opts.NhlModelMode <- "fallback"
    opts.NhlModelRefreshEnabled <- true
    opts.ApprovedSeasons <- [|2027|]
    Assert.True(opts.MoneyPuckRefreshEnabledFor("nhl", 2027))
    Assert.False(opts.MoneyPuckRefreshEnabledFor("nhl", 2028))
    Assert.False(opts.MoneyPuckRefreshEnabledFor("nba", 2027))
    Assert.Equal("off", opts.ModeFor("nhl", 2027))
    Assert.False(opts.RefreshEnabledFor("nhl", 2027))
    let legacy = model ()
    legacy.Source <- "playoffstatus"
    Assert.True((PlayoffRatingPolicy.select { request () with Options = opts } [legacy]).Probability.IsNone)
    Assert.False(PlayoffRatingsOptions().MoneyPuckSelectionEnabledFor("nhl", 2027))
    Assert.False(PlayoffRatingsOptions().MoneyPuckRefreshEnabledFor("nhl", 2027))

[<Theory>]
[<InlineData("matching", true)>]
[<InlineData("missing", false)>]
[<InlineData("future", false)>]
[<InlineData("rejected", false)>]
let ``V4 estimates require a matching trusted publication boundary`` boundary eligible =
    let row = model ()
    changeEvidence row (fun e ->
        e.Version <- 4
        e.PublicationBoundary <-
            match boundary with
            | "matching" | "rejected" -> e.PublishedAt
            | "future" -> Nullable(now.AddYears(5))
            | _ -> Nullable()
        if boundary = "rejected" then e.MoneyPuckRejectedPublications <- [| e.PublishedAt.Value |])
    Assert.Equal(eligible, (PlayoffRatingPolicy.select (request ()) [row]).Probability.IsSome)

[<Theory>]
[<InlineData("version")>]
[<InlineData("kind")>]
[<InlineData("provider")>]
[<InlineData("policy")>]
[<InlineData("provider_unverified")>]
[<InlineData("sport")>]
[<InlineData("season")>]
[<InlineData("season_unverified")>]
[<InlineData("scalar_season")>]
[<InlineData("team")>]
[<InlineData("team_unverified")>]
[<InlineData("unknown_team")>]
[<InlineData("noncanonical_team")>]
[<InlineData("accepted_count")>]
[<InlineData("total_count")>]
[<InlineData("oversized_count")>]
[<InlineData("state")>]
[<InlineData("reason")>]
[<InlineData("forged_final")>]
[<InlineData("legacy_json")>]
let ``Only verified complete estimate envelopes are eligible MoneyPuck evidence`` invalid =
    let row = model ()
    let mutable req = request ()
    changeEvidence row (fun e ->
        match invalid with
        | "version" -> e.Version <- 2
        | "kind" -> e.EvidenceKind <- "market_quote"
        | "provider" -> e.Provider <- "moneypuck"
        | "policy" -> e.ValidationPolicy <- "playoffstatus-nhl-qualification-model-v2"
        | "sport" -> e.Sport <- "nba"
        | "season" -> e.Season <- 2026
        | "season_unverified" -> e.SeasonVerified <- false
        | "scalar_season" -> row.SourceSeason <- Nullable()
        | "team" -> e.Team <- "OTT"
        | "team_unverified" -> e.TeamVerified <- false
        | "unknown_team" -> e.Team <- "UNKNOWN"; row.TeamAbbreviation <- "UNKNOWN"; req <- { req with Team = "UNKNOWN" }
        | "noncanonical_team" -> e.Team <- "UTA"; row.TeamAbbreviation <- "UTA"; req <- { req with Team = "UTAH" }
        | "accepted_count" -> e.AcceptedTeamCount <- 31
        | "total_count" -> e.TotalTeamCount <- 31
        | "oversized_count" -> e.AcceptedTeamCount <- 33; e.TotalTeamCount <- 33
        | "state" -> e.State <- "final"
        | "reason" -> e.ReasonCode <- "invalid_snapshot"
        | "forged_final" -> row.PlayoffProbabilityIsFinal <- true
        | _ -> ())
    if invalid = "provider_unverified" then
        row.PlayoffEvidenceJson <- row.PlayoffEvidenceJson.Replace("\"ProviderVerified\":true", "\"ProviderVerified\":false")
    if invalid = "legacy_json" then row.PlayoffEvidenceJson <- "{\"Probability\":0.2518}"
    Assert.True((PlayoffRatingPolicy.select req [row]).Probability.IsNone, invalid)

[<Theory>]
[<InlineData("missing_publication")>]
[<InlineData("missing_fetch")>]
[<InlineData("date_only_publication")>]
[<InlineData("local_publication")>]
[<InlineData("local_fetch")>]
[<InlineData("local_scalar")>]
[<InlineData("minimum_publication")>]
[<InlineData("maximum_publication")>]
[<InlineData("publication_after_fetch_tick")>]
[<InlineData("fetch_after_now_tick")>]
[<InlineData("scalar_after_now_tick")>]
[<InlineData("later_fetch_bucket")>]
[<InlineData("earlier_fetch_bucket")>]
let ``MoneyPuck requires full UTC clocks and fetch identity at persisted microsecond precision`` invalid =
    let row = model ()
    changeEvidence row (fun e ->
        match invalid with
        | "missing_publication" -> e.PublishedAt <- Nullable()
        | "missing_fetch" -> e.FetchedAt <- Nullable()
        | "date_only_publication" -> e.PublishedAt <- Nullable(DateTime(2026, 10, 10))
        | "local_publication" -> e.PublishedAt <- Nullable(DateTime.SpecifyKind(published, DateTimeKind.Local))
        | "local_fetch" -> e.FetchedAt <- Nullable(DateTime.SpecifyKind(now, DateTimeKind.Local))
        | "local_scalar" -> row.FetchedAt <- DateTime.SpecifyKind(now, DateTimeKind.Local)
        | "minimum_publication" -> e.PublishedAt <- Nullable(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc))
        | "maximum_publication" -> e.PublishedAt <- Nullable(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc))
        | "publication_after_fetch_tick" -> e.PublishedAt <- Nullable(now.AddTicks(1L))
        | "fetch_after_now_tick" -> e.FetchedAt <- Nullable(now.AddTicks(1L))
        | "scalar_after_now_tick" -> row.FetchedAt <- now.AddTicks(1L)
        | "later_fetch_bucket" -> e.FetchedAt <- Nullable(now.AddTicks(10L))
        | "earlier_fetch_bucket" -> e.FetchedAt <- Nullable(now.AddTicks(-10L))
        | _ -> failwith invalid)
    Assert.True((select [row]).Probability.IsNone, invalid)

[<Theory>]
[<InlineData("missing")>]
[<InlineData("nan")>]
[<InlineData("infinity")>]
[<InlineData("negative")>]
[<InlineData("over_one")>]
let ``Invalid MoneyPuck probabilities stay unavailable with an honest reason`` invalid =
    let row = model ()
    row.PlayoffProbability <-
        match invalid with
        | "missing" -> Nullable()
        | "nan" -> Nullable Double.NaN
        | "infinity" -> Nullable Double.PositiveInfinity
        | "negative" -> Nullable -0.001
        | _ -> Nullable 1.001
    let selected = select [row]
    Assert.True(selected.Probability.IsNone)
    Assert.Equal("missing_probability", selected.Providers[1].Reason)

[<Fact>]
let ``Request selection cache distinguishes MoneyPuck enablement and rollback without deleting evidence`` () =
    let cache = PlayoffRatingPolicy.PlayoffSelectionCache(TimeProvider.System)
    let row = model ()
    let disabled = { request () with Options = PlayoffRatingsOptions() }
    Assert.True((cache.Select(disabled, [row])).Probability.IsNone)
    Assert.Equal(Some 0.2518, (cache.Select(request (), [row])).Probability)
    Assert.True((cache.Select(disabled, [row])).Probability.IsNone)
    Assert.Equal(Nullable 0.2518, row.PlayoffProbability)

[<Theory>]
[<InlineData(-1L, true)>]
[<InlineData(0L, false)>]
[<InlineData(1L, false)>]
let ``MoneyPuck expires exactly thirty six hours from publication even after unchanged refetches`` ticks eligible =
    let row = model ()
    let expires = published.AddHours(36.0)
    let req = { request () with Now = expires.AddTicks(ticks) }
    Assert.Equal(eligible, (PlayoffRatingPolicy.select req [row]).Probability.IsSome)
    row.FetchedAt <- req.Now
    changeEvidence row (fun e -> e.FetchedAt <- Nullable req.Now)
    let selected = PlayoffRatingPolicy.select req [row]
    Assert.Equal(eligible, selected.Probability.IsSome)
    Assert.Equal(Some expires, selected.Providers[1].ExpiresAt)

[<Theory>]
[<InlineData("missing_proof")>]
[<InlineData("wrong_page_label")>]
[<InlineData("wrong_page_start")>]
[<InlineData("wrong_page_end")>]
[<InlineData("wrong_page_reference")>]
[<InlineData("missing_page_observation")>]
[<InlineData("future_page_observation")>]
[<InlineData("missing_id")>]
[<InlineData("no_games")>]
[<InlineData("unknown_id")>]
[<InlineData("substituted_id")>]
[<InlineData("wrong_nhl_season")>]
[<InlineData("prefix_without_season")>]
[<InlineData("wrong_nhl_reference")>]
[<InlineData("missing_nhl_observation")>]
[<InlineData("future_nhl_observation")>]
[<InlineData("duplicate_proof")>]
[<InlineData("other_game_wrong_season")>]
let ``MoneyPuck season eligibility requires explicit page and exact NHL game season proof not an ID prefix`` invalid =
    let row = model ()
    let json = JsonNode.Parse(row.PlayoffEvidenceJson)
    let proof = json["MoneyPuckSeasonProof"]
    let games = proof["VerifiedGames"].AsArray()
    let game = games[0]
    match invalid with
    | "missing_proof" -> json.AsObject().Remove("MoneyPuckSeasonProof") |> ignore
    | "wrong_page_label" -> proof["PageSeasonLabel"] <- JsonValue.Create("2025–26")
    | "wrong_page_start" -> proof["PageSeasonStartYear"] <- JsonValue.Create(2025)
    | "wrong_page_end" -> proof["PageSeasonEndYear"] <- JsonValue.Create(2026)
    | "wrong_page_reference" -> proof["PageReference"] <- JsonValue.Create("https://moneypuck.com/rankings.htm")
    | "missing_page_observation" -> proof.AsObject().Remove("PageObservedAt") |> ignore
    | "future_page_observation" -> proof["PageObservedAt"] <- JsonValue.Create(now.AddTicks(1L))
    | "missing_id" -> proof.AsObject().Remove("FirstSimulatedId") |> ignore
    | "no_games" -> games.Clear()
    | "unknown_id" -> proof["FirstSimulatedId"] <- JsonValue.Create(2099020003L)
    | "substituted_id" -> game["GameId"] <- JsonValue.Create(2099020003L)
    | "wrong_nhl_season" -> game["Season"] <- JsonValue.Create(20252026)
    | "prefix_without_season" -> game.AsObject().Remove("Season") |> ignore
    | "wrong_nhl_reference" -> game["Reference"] <- JsonValue.Create("https://api-web.nhle.com/v1/gamecenter/2099020003/landing")
    | "missing_nhl_observation" -> game.AsObject().Remove("ObservedAt") |> ignore
    | "future_nhl_observation" -> game["ObservedAt"] <- JsonValue.Create(now.AddTicks(1L))
    | "duplicate_proof" -> games.Add(game.DeepClone())
    | "other_game_wrong_season" ->
        let other = game.DeepClone()
        other["SimulatedId"] <- JsonValue.Create(2099020003L)
        other["GameId"] <- JsonValue.Create(2099020003L)
        other["Season"] <- JsonValue.Create(20252026)
        other["Reference"] <- JsonValue.Create("https://api-web.nhle.com/v1/gamecenter/2099020003/landing")
        games.Add(other)
    | _ -> failwith invalid
    row.PlayoffEvidenceJson <- json.ToJsonString()
    Assert.True((select [row]).Probability.IsNone, invalid)

[<Fact>]
let ``Kalshi wins independently for Ottawa while MoneyPuck fills Nashville wide spread gap without blending`` () =
    let nashville = quote "NSH" 0.27M 0.74M now
    let ottawa = quote "OTT" 0.53M 0.62M (now.AddMinutes(-5.0))
    let ottawaModel = model ()
    ottawaModel.TeamAbbreviation <- "OTT"
    ottawaModel.PlayoffProbability <- Nullable 0.6804
    changeEvidence ottawaModel (fun e -> e.Team <- "OTT")
    let rows = [nashville; ottawa; ottawaModel; model ()]
    let nsh = select rows
    Assert.Equal(Some "moneypuck_qualification", nsh.Source)
    Assert.Equal(Some 0.2518, nsh.Probability)
    Assert.Equal("wide_spread", nsh.Providers[0].Reason)
    let ott = PlayoffRatingPolicy.select { request () with Team = "OTT" } rows
    Assert.Equal(Some "kalshi", ott.Source)
    Assert.Equal(Some 0.575, ott.Probability)

[<Fact>]
let ``MoneyPuck fills an expired Kalshi estimate but cannot bypass its trusted resolution blocks`` () =
    let k = quote "NSH" 0.53M 0.62M (now.AddHours(-1.0))
    Assert.Equal(Some 0.2518, (select [k; model ()]).Probability)
    for reason in ["exceptional_settlement"; "final_retracted"; "rules_changed"; "disputed_resolution"; "void_resolution"] do
        let evidence = PlayoffQuoteEvidence.Parse(k.PlayoffEvidenceJson)
        evidence.State <- "resolution_blocked"
        evidence.ResolutionBlocked <- true
        evidence.ReasonCode <- reason
        k.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)
        let blocked = select [k; model ()]
        Assert.True(blocked.Probability.IsNone, reason)
        Assert.Equal("resolution_blocked", blocked.Reason)

[<Theory>]
[<InlineData(0.0)>]
[<InlineData(1.0)>]
let ``Verified Kalshi finals win indefinitely and MoneyPuck endpoints remain estimates`` probability =
    let row = model ()
    row.PlayoffProbability <- Nullable probability
    let estimated = select [row]
    Assert.Equal(Some probability, estimated.Probability)
    Assert.False(estimated.IsFinal)
    let k = quote "NSH" 0.53M 0.62M (now.AddYears(-1))
    k.PlayoffProbability <- Nullable(1.0 - probability)
    k.PlayoffProbabilityIsFinal <- true
    let evidence = PlayoffQuoteEvidence.Parse(k.PlayoffEvidenceJson)
    evidence.State <- "final"
    evidence.YesPayout <- Nullable(decimal (1.0 - probability))
    evidence.NoPayout <- Nullable(decimal probability)
    evidence.ResolutionAt <- evidence.ObservedAt
    evidence.ResolutionReference <- "synthetic-verified-resolution"
    k.PlayoffEvidenceJson <- JsonSerializer.Serialize(evidence)
    let selected = select [row; k]
    Assert.Equal(Some "kalshi", selected.Source)
    Assert.Equal(Some(1.0 - probability), selected.Probability)
    Assert.True(selected.IsFinal)
    Assert.True(selected.ExpiresAt.IsNone)

[<Fact>]
let ``Latest rejected MoneyPuck snapshot revokes older accepted evidence including equal clock ID ties`` () =
    let old = model ()
    old.Id <- 1
    let rejected = model ()
    rejected.Id <- 2
    changeEvidence rejected (fun e -> e.State <- "unavailable"; e.ReasonCode <- "invalid_snapshot")
    let selected = select [old; rejected]
    Assert.True(selected.Probability.IsNone)
    Assert.Equal("invalid_snapshot", selected.Providers[1].Reason)
    rejected.FetchedAt <- now.AddMinutes(-1.0)
    Assert.Equal(Some 0.2518, (select [rejected; old]).Probability)

[<Fact>]
let ``All canonical NHL teams are eligible and scalar Utah alias resolves to canonical evidence`` () =
    let teams = ["ANA"; "BOS"; "BUF"; "CAR"; "CBJ"; "CGY"; "CHI"; "COL"; "DAL"; "DET"; "EDM"; "FLA"; "LA"; "MIN"; "MTL"; "NJ";
                 "NSH"; "NYI"; "NYR"; "OTT"; "PHI"; "PIT"; "SEA"; "SJ"; "STL"; "TB"; "TOR"; "UTAH"; "VAN"; "VGK"; "WPG"; "WSH"]
    for team in teams do
        let row = model ()
        row.TeamAbbreviation <- if team = "UTAH" then "UTA" else team
        changeEvidence row (fun e -> e.Team <- team)
        let selected = PlayoffRatingPolicy.select { request () with Team = team } [row]
        Assert.Equal(Some 0.2518, selected.Probability)

[<Theory>]
[<InlineData(0L)>]
[<InlineData(1L)>]
[<InlineData(2L)>]
[<InlineData(3L)>]
[<InlineData(4L)>]
[<InlineData(5L)>]
[<InlineData(6L)>]
[<InlineData(7L)>]
[<InlineData(8L)>]
[<InlineData(9L)>]
let ``MoneyPuck JSON ticks survive persisted scalar microseconds without extending publication expiry`` remainder =
    let row = model ()
    changeEvidence row (fun e -> e.FetchedAt <- Nullable(now.AddTicks(remainder)))
    let req = { request () with Now = now.AddMinutes(1.0) }
    let selected = PlayoffRatingPolicy.select req [row]
    Assert.Equal(Some 0.2518, selected.Probability)
    Assert.Equal(Some(published.AddHours(36.0)), selected.ExpiresAt)

[<Fact>]
let ``Explicit season proof supports multiple exact IDs and four normalized page label formats`` () =
    for label in ["2026–27"; "2026-27"; "2026–2027"; "2026-2027"] do
        let row = model ()
        changeEvidence row (fun e ->
            let proof = e.MoneyPuckSeasonProof
            proof.PageSeasonLabel <- label
            let first = proof.VerifiedGames[0]
            let second = NhlSimulatedGameSeasonEvidence(SimulatedId = 2099020003L, GameId = 2099020003L,
                                                       Season = 20262027, ObservedAt = first.ObservedAt,
                                                       Reference = "https://api-web.nhle.com/v1/gamecenter/2099020003/landing")
            proof.VerifiedGames <- [|first; second|]
            proof.FirstSimulatedId <- 2099020003L)
        Assert.Equal(Some 0.2518, (select [row]).Probability)
