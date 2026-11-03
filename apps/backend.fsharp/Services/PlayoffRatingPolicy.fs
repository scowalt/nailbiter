namespace backend.Services

open System
open System.Collections.Generic
open backend.Models

module PlayoffRatingPolicy =
    let sports = [ "nfl"; "nba"; "nhl"; "mlb"; "cfb" ]
    let isSupported (sport: string) = List.contains sport sports
    let refreshInterval = TimeSpan.FromMinutes(5.0)
    let maxAge = TimeSpan.FromHours(1.0)
    let modelMaxAge = TimeSpan.FromHours(36.0)
    let modelRefreshCooldown = TimeSpan.FromHours(6.0)
    [<Literal>]
    let collegeNoMarketSource = "cfp_no_market"
    [<Literal>]
    let kalshiSource = "kalshi"
    [<Literal>]
    let playoffStatusSource = "playoffstatus"
    [<Literal>]
    let playoffStatusValidationPolicy = "playoffstatus-nhl-qualification-model-v2"
    [<Literal>]
    let moneyPuckQualificationSource = "moneypuck_qualification"
    [<Literal>]
    let moneyPuckQualificationValidationPolicy = "moneypuck-nhl-qualification-model-v1"
    let moneyPuckQualificationMaxAge = TimeSpan.FromHours(36.0)
    [<Literal>]
    let moneyPuckQualificationEvidenceVersion = 4
    [<Literal>]
    let collegeNoMarketProbability = 0.005

    let seasonAt (sport: string) (date: DateTime) =
        match sport with
        | "cfb" -> CollegeFootballPolicy.SeasonAt(date)
        | "nfl" -> if date.Month >= 3 then date.Year else date.Year - 1
        | "nba" | "nhl" -> if date.Month >= 10 then date.Year + 1 else date.Year
        | _ -> date.Year

    let gameSeason (game: Game) =
        game.SeasonYear |> Option.ofNullable |> Option.defaultWith (fun () -> seasonAt game.Sport game.ScheduledStart)

    let refreshSeasons (sport: string) (date: DateTime) =
        let season = seasonAt sport date
        match sport with
        | "nba" | "nhl" when date.Month >= 7 && date.Month < 10 -> [ season; season + 1 ]
        | "mlb" when date.Month >= 11 -> [ season; season + 1 ]
        | _ -> [ season ]

    type Snapshot =
        { Probability: float option
          FetchedAt: DateTime
          EvidenceAt: DateTime
          ExpiresAt: DateTime option
          Source: string option
          IsFinal: bool
          IsAssumed: bool
          Reason: string }

    type SelectionRequest =
        { Sport: string
          Season: int
          Team: string
          Now: DateTime
          Options: PlayoffRatingsOptions }

    type ProviderAssessment =
        { Source: string
          Probability: float option
          IsFinal: bool
          IsAssumed: bool
          EvidenceAt: DateTime option
          FetchedAt: DateTime option
          ExpiresAt: DateTime option
          Reason: string
          ResolutionBlocked: bool }

    type Selection =
        { Source: string option
          Probability: float option
          IsFinal: bool
          IsAssumed: bool
          EvidenceAt: DateTime option
          FetchedAt: DateTime option
          ExpiresAt: DateTime option
          Reason: string
          Providers: ProviderAssessment list }

    let private teamKey sport (team: string) =
        if String.IsNullOrWhiteSpace team then "" else backend.Scoring.ProHypePolicy.TeamKey(sport, team)

    let private latest rows =
        rows |> Seq.sortByDescending (fun (r: TeamRating) -> struct (r.FetchedAt, r.Id)) |> Seq.tryHead

    let private missing source reason : ProviderAssessment =
        { Source = source
          Probability = None
          IsFinal = false
          IsAssumed = false
          EvidenceAt = None
          FetchedAt = None
          ExpiresAt = None
          Reason = reason
          ResolutionBlocked = false }

    let private finiteProbability (value: Nullable<float>) =
        value
        |> Option.ofNullable
        |> Option.filter (fun p -> Double.IsFinite(p) && p >= 0.0 && p <= 1.0)

    let private trustedResolutionReason reason =
        List.contains reason [ "exceptional_settlement"; "final_retracted"; "rules_changed"; "disputed_resolution"; "void_resolution" ]

    let private afterPersistedTimestamp (jsonAt: DateTime) (persistedAt: DateTime) =
        jsonAt.Ticks / TimeSpan.TicksPerMicrosecond > persistedAt.Ticks / TimeSpan.TicksPerMicrosecond

    let private kalshiEnvelopeProblem (request: SelectionRequest) (row: TeamRating) (envelope: PlayoffQuoteEvidence) =
        let mutable evidenceAt = row.FetchedAt
        let mutable resolutionBlocked = false
        let reject reason = Some(reason, evidenceAt, resolutionBlocked)
        if isNull envelope then
            if not (String.IsNullOrWhiteSpace row.PlayoffEvidenceJson) then reject "unverified_evidence" else None
        elif envelope.Version <> 1 || envelope.Provider <> kalshiSource || envelope.Sport <> request.Sport
             || envelope.Season <> request.Season || teamKey request.Sport envelope.Team <> teamKey request.Sport request.Team
             || not envelope.ContractVerified then reject "identity_mismatch"
        elif not envelope.ObservedAt.HasValue || envelope.ObservedAt.Value = DateTime.MinValue then reject "unverified_evidence"
        elif envelope.ObservedAt.Value > request.Now || afterPersistedTimestamp envelope.ObservedAt.Value row.FetchedAt then reject "future_observation"
        else
            let ticker = $"KXNHLPLAYOFF-{request.Season % 100:D2}"
            let suffix = if teamKey "nhl" request.Team = "UTAH" then "UTA" else request.Team
            if request.Sport = "nhl" && (envelope.ValidationPolicy <> "kalshi-qualification-v1" || envelope.EventId <> ticker
                || (envelope.MarketId <> ticker + "-" + suffix && envelope.MarketId <> ticker + "-" + teamKey "nhl" request.Team)) then
                reject "unsupported_contract"
            elif envelope.ResolutionBlocked || envelope.State = "resolution_blocked" then
                resolutionBlocked <- envelope.ResolutionBlocked && envelope.State = "resolution_blocked" && trustedResolutionReason envelope.ReasonCode
                reject (if resolutionBlocked then envelope.ReasonCode else "unverified_evidence")
            elif envelope.State <> "estimate" && envelope.State <> "final" then reject envelope.ReasonCode
            elif not envelope.EvidenceAt.HasValue then reject "unverified_evidence"
            else
                evidenceAt <- envelope.EvidenceAt.Value
                if evidenceAt > envelope.ObservedAt.Value || afterPersistedTimestamp evidenceAt row.FetchedAt then reject "future_observation"
                elif envelope.State = "final" then
                    let value = finiteProbability row.PlayoffProbability
                    if row.PlayoffProbabilityIsFinal && (value = Some 0.0 || value = Some 1.0)
                       && envelope.YesPayout = Nullable(decimal value.Value)
                       && envelope.NoPayout = Nullable(1M - decimal value.Value)
                       && not (String.IsNullOrWhiteSpace envelope.ResolutionReference)
                       && (not envelope.ResolutionAt.HasValue || envelope.ResolutionAt.Value <= envelope.ObservedAt.Value) then None
                    else reject "unverified_final"
                elif (envelope.BidSize = Nullable 0M && not envelope.BidExplicitlyEmpty)
                     || (envelope.AskSize = Nullable 0M && not envelope.AskExplicitlyEmpty)
                     || (envelope.BidExplicitlyEmpty && (envelope.Bid <> Nullable 0M || envelope.BidSize <> Nullable 0M))
                     || (envelope.AskExplicitlyEmpty && (envelope.Ask <> Nullable 1M || envelope.AskSize <> Nullable 0M)) then reject "insufficient_depth"
                else
                    match QualificationQuotePolicy.evaluate (Option.ofNullable envelope.Bid) (Option.ofNullable envelope.Ask)
                              (Option.ofNullable envelope.BidSize) (Option.ofNullable envelope.AskSize) with
                    | Ok p when row.PlayoffProbability = Nullable(float p) && not row.PlayoffProbabilityIsFinal -> None
                    | Ok _ -> reject "inconsistent_projection"
                    | Error reason -> reject reason

    let private assessKalshi (request: SelectionRequest) row =
        let empty = missing kalshiSource "missing_market"
        match row with
        | None -> empty
        | Some (r: TeamRating) ->
            let observed = { empty with FetchedAt = Some r.FetchedAt; EvidenceAt = Some r.FetchedAt; ExpiresAt = Some(r.FetchedAt.Add(maxAge)) }
            let reject reason = { observed with Reason = reason }
            if r.Source = collegeNoMarketSource then
                let value =
                    r.PlayoffProbability
                    |> finiteProbability
                    |> Option.filter (fun p -> request.Sport = "cfb" && p = collegeNoMarketProbability && not r.PlayoffProbabilityIsFinal)
                if r.SourceSeason <> Nullable request.Season then reject "unverified_season"
                elif r.FetchedAt > request.Now then reject "future_observation"
                elif value.IsSome && request.Now - r.FetchedAt < maxAge then { observed with Probability = value; IsAssumed = true; Reason = "accepted" }
                elif value.IsSome then reject "expired"
                else reject "missing_probability"
            elif r.SourceSeason <> Nullable request.Season then reject "unverified_season"
            elif r.FetchedAt > request.Now then reject "future_observation"
            else
                let envelope = PlayoffQuoteEvidence.Parse(r.PlayoffEvidenceJson)
                match if isNull envelope && r.PlayoffProbabilityIsFinal && request.Sport = "nhl" then Some("unverified_final", r.FetchedAt, false) else kalshiEnvelopeProblem request r envelope with
                | Some(reason, evidenceAt, resolutionBlocked) ->
                    { reject reason with EvidenceAt = Some evidenceAt; ExpiresAt = Some(evidenceAt.Add(maxAge)); ResolutionBlocked = resolutionBlocked }
                | None ->
                    let evidenceAt = if isNull envelope || not envelope.EvidenceAt.HasValue then r.FetchedAt else envelope.EvidenceAt.Value
                    let observed = { observed with EvidenceAt = Some evidenceAt; ExpiresAt = Some(evidenceAt.Add(maxAge)) }
                    let value = finiteProbability r.PlayoffProbability
                    if value.IsNone then { observed with Reason = "missing_probability" }
                    elif r.PlayoffProbabilityIsFinal then
                        if value = Some 0.0 || value = Some 1.0 then
                            { observed with Probability = value; IsFinal = true; Reason = "accepted"; ExpiresAt = None }
                        else { observed with Reason = "unverified_final" }
                    elif request.Now - evidenceAt >= maxAge then { observed with Reason = "expired" }
                    else { observed with Probability = value; Reason = "accepted" }

    let private assessPlayoffStatus (request: SelectionRequest) row =
        let empty = missing playoffStatusSource "missing_model"
        match row with
        | None -> empty
        | Some (r: TeamRating) ->
            let observed = { empty with FetchedAt = Some r.FetchedAt }
            let reject publishedAt reason = { observed with EvidenceAt = publishedAt; ExpiresAt = publishedAt |> Option.map (fun at -> at.Add(modelMaxAge)); Reason = reason }
            if request.Sport <> "nhl" then reject None "unsupported_sport"
            elif r.SourceSeason <> Nullable request.Season then reject None "unverified_season"
            elif r.FetchedAt > request.Now then reject (Some r.FetchedAt) "future_observation"
            else
                let evidence = PlayoffModelEvidence.Parse(r.PlayoffEvidenceJson)
                if isNull evidence then reject None "unverified_model_evidence"
                elif evidence.Version <> 2 || evidence.EvidenceKind <> "model_projection" then reject None "unverified_model_evidence"
                elif evidence.Provider <> playoffStatusSource || evidence.ValidationPolicy <> playoffStatusValidationPolicy
                     || evidence.Sport <> request.Sport || evidence.Season <> request.Season
                     || teamKey request.Sport evidence.Team <> teamKey request.Sport request.Team
                     || not evidence.SeasonVerified || not evidence.TeamVerified then reject None "identity_mismatch"
                elif not evidence.PublishedAt.HasValue || not evidence.FetchedAt.HasValue then reject None "unverified_publication"
                else
                    let publishedAt = evidence.PublishedAt.Value
                    let fetchedAt = evidence.FetchedAt.Value
                    if publishedAt > fetchedAt || afterPersistedTimestamp fetchedAt r.FetchedAt || fetchedAt > request.Now || publishedAt > request.Now then
                        reject (Some publishedAt) "future_observation"
                    elif evidence.State <> "estimate" then
                        reject (Some publishedAt) (if String.IsNullOrWhiteSpace evidence.ReasonCode then "unavailable" else evidence.ReasonCode)
                    elif r.PlayoffProbabilityIsFinal then reject (Some publishedAt) "model_cannot_be_final"
                    else
                        let value = finiteProbability r.PlayoffProbability
                        if value.IsNone then reject (Some publishedAt) "missing_probability"
                        elif request.Now - publishedAt >= modelMaxAge then reject (Some publishedAt) "expired"
                        elif evidence.ReasonCode <> "accepted" then reject (Some publishedAt) evidence.ReasonCode
                        else
                            { observed with Probability = value
                                            EvidenceAt = Some publishedAt
                                            ExpiresAt = Some(publishedAt.Add(modelMaxAge))
                                            Reason = "accepted" }

    let private verifiedMoneyPuckSeason (season: int) (fetchedAt: DateTime) (proof: MoneyPuckSeasonProof) =
        let clock (at: Nullable<DateTime>) =
            at.HasValue && at.Value.Kind = DateTimeKind.Utc && at.Value <> DateTime.MinValue && at.Value <= fetchedAt
        if isNull proof || proof.PageSeasonStartYear <> season - 1 || proof.PageSeasonEndYear <> season
           || not (List.contains proof.PageSeasonLabel [ $"{season - 1}–{season % 100:D2}"; $"{season - 1}-{season % 100:D2}";
                                                        $"{season - 1}–{season}"; $"{season - 1}-{season}" ])
           || proof.PageReference <> "https://moneypuck.com/predictions.htm" || not (clock proof.PageObservedAt)
           || isNull proof.VerifiedGames || proof.VerifiedGames.Length < 1 || proof.VerifiedGames.Length > 32 then false
        else
            let ids = HashSet<int64>()
            let valid =
                proof.VerifiedGames |> Array.forall (fun game ->
                    not (isNull game) && game.SimulatedId >= 1000000000L && game.SimulatedId <= 9999999999L
                    && ids.Add(game.SimulatedId) && game.GameId = game.SimulatedId
                    && game.Season = (season - 1) * 10000 + season
                    && game.Reference = $"https://api-web.nhle.com/v1/gamecenter/{game.SimulatedId}/landing"
                    && clock game.ObservedAt)
            valid && ids.Contains(proof.FirstSimulatedId)

    let private assessMoneyPuck (request: SelectionRequest) row =
        let empty = missing moneyPuckQualificationSource "missing_model"
        match row with
        | None -> empty
        | Some (r: TeamRating) ->
            let observed = { empty with FetchedAt = Some r.FetchedAt }
            let reject reason = { observed with Reason = reason }
            let evidence = PlayoffModelEvidence.Parse(r.PlayoffEvidenceJson)
            let ageOnly = not (isNull evidence) && evidence.Version = moneyPuckQualificationEvidenceVersion
                          && evidence.State = "unavailable" && evidence.ReasonCode = "expired"
                          && (finiteProbability evidence.MoneyPuckExpiredProbability).IsSome
                          && not r.PlayoffProbability.HasValue && not r.PlayoffProbabilityIsFinal
                          && evidence.AcceptedTeamCount = 0 && evidence.TotalTeamCount = 32
            if r.SourceSeason <> Nullable request.Season then reject "unverified_season"
            elif isNull evidence || (evidence.Version <> 3 && evidence.Version <> moneyPuckQualificationEvidenceVersion) || evidence.EvidenceKind <> "model_projection" then
                reject "unverified_model_evidence"
            elif evidence.Provider <> moneyPuckQualificationSource || not evidence.ProviderVerified
                 || evidence.ValidationPolicy <> moneyPuckQualificationValidationPolicy
                 || evidence.Sport <> "nhl" || evidence.Season <> request.Season || (not evidence.SeasonVerified && not ageOnly)
                 || not evidence.TeamVerified || not (NhlQualificationRoster.teams.Contains evidence.Team)
                 || evidence.Team <> teamKey "nhl" request.Team then reject "identity_mismatch"
            elif not evidence.PublishedAt.HasValue || not evidence.FetchedAt.HasValue
                 || evidence.PublishedAt.Value.Kind <> DateTimeKind.Utc || evidence.PublishedAt.Value = DateTime.MinValue
                 || evidence.FetchedAt.Value.Kind <> DateTimeKind.Utc || evidence.FetchedAt.Value = DateTime.MinValue
                 || r.FetchedAt.Kind <> DateTimeKind.Utc || r.FetchedAt = DateTime.MinValue then reject "unverified_publication"
            elif evidence.PublishedAt.Value > evidence.FetchedAt.Value || evidence.FetchedAt.Value > request.Now
                 || r.FetchedAt > request.Now then reject "future_observation"
            elif evidence.Version = moneyPuckQualificationEvidenceVersion && (evidence.State = "estimate" || ageOnly)
                 && evidence.PublicationBoundary <> evidence.PublishedAt then reject "unverified_publication_boundary"
            elif evidence.Version = moneyPuckQualificationEvidenceVersion && not (isNull evidence.MoneyPuckRejectedPublications)
                 && Array.contains evidence.PublishedAt.Value evidence.MoneyPuckRejectedPublications then reject "rejected_publication"
            elif not (verifiedMoneyPuckSeason request.Season evidence.FetchedAt.Value evidence.MoneyPuckSeasonProof) then
                reject "unverified_season_proof"
            elif evidence.FetchedAt.Value.Ticks / TimeSpan.TicksPerMicrosecond <> r.FetchedAt.Ticks / TimeSpan.TicksPerMicrosecond then
                reject "fetch_identity_mismatch"
            else
                let observed = { observed with EvidenceAt = Some evidence.PublishedAt.Value
                                               ExpiresAt = Some(evidence.PublishedAt.Value.Add(moneyPuckQualificationMaxAge)) }
                if ageOnly && request.Now - evidence.PublishedAt.Value >= moneyPuckQualificationMaxAge then
                    { observed with Reason = "expired" }
                elif evidence.State <> "estimate" || evidence.ReasonCode <> "accepted" then
                    { observed with Reason = if String.IsNullOrWhiteSpace evidence.ReasonCode || evidence.ReasonCode = "accepted" then "unavailable" else evidence.ReasonCode }
                elif evidence.AcceptedTeamCount <> 32 || evidence.TotalTeamCount <> 32 then
                    { observed with Reason = "incomplete_snapshot" }
                elif r.PlayoffProbabilityIsFinal then { observed with Reason = "model_cannot_be_final" }
                elif (finiteProbability r.PlayoffProbability).IsNone then { observed with Reason = "missing_probability" }
                elif request.Now - evidence.PublishedAt.Value >= moneyPuckQualificationMaxAge then { observed with Reason = "expired" }
                else
                    let value = finiteProbability r.PlayoffProbability
                    { observed with Probability = value; Reason = if value.IsSome then "accepted" else "missing_probability" }

    let private finish providers selected reason =
        match selected with
        | Some (s: ProviderAssessment) ->
            { Source = Some s.Source
              Probability = s.Probability
              IsFinal = s.IsFinal
              IsAssumed = s.IsAssumed
              EvidenceAt = s.EvidenceAt
              FetchedAt = s.FetchedAt
              ExpiresAt = s.ExpiresAt
              Reason = reason
              Providers = providers }
        | None ->
            { Source = None
              Probability = None
              IsFinal = false
              IsAssumed = false
              EvidenceAt = None
              FetchedAt = providers |> List.tryPick (fun p -> p.FetchedAt)
              ExpiresAt = None
              Reason = reason
              Providers = providers }

    let select (request: SelectionRequest) (ratings: seq<TeamRating>) : Selection =
        let rows =
            ratings
            |> Seq.filter (fun r -> r.Sport = request.Sport && r.Season = request.Season
                                    && teamKey request.Sport r.TeamAbbreviation = teamKey request.Sport request.Team)
            |> Seq.toArray
        let mode = request.Options.ModeFor(request.Sport, request.Season)
        let kalshiRow =
            rows
            |> Seq.filter (fun r -> r.Source = kalshiSource || (request.Sport = "cfb" && r.Source = collegeNoMarketSource))
            |> latest
        let kalshi = assessKalshi request kalshiRow
        let models =
            [ if request.Options.MoneyPuckSelectionEnabledFor(request.Sport, request.Season) then
                  yield rows |> Seq.filter (fun r -> r.Source = moneyPuckQualificationSource) |> latest |> assessMoneyPuck request
              if mode = "fallback" then
                  yield rows |> Seq.filter (fun r -> r.Source = playoffStatusSource) |> latest |> assessPlayoffStatus request ]
        let providers = kalshi :: models
        if models.IsEmpty then
            finish providers (if kalshi.Probability.IsSome then Some kalshi else None) kalshi.Reason
        elif kalshi.ResolutionBlocked then finish providers None "resolution_blocked"
        elif kalshi.Probability.IsSome then finish providers (Some kalshi) "accepted"
        else
            let selected = models |> List.tryFind (fun model -> model.Probability.IsSome)
            finish providers selected (if selected.IsSome then "accepted" else "unavailable")

    let selectionToSnapshot (selection: Selection) =
        let provider =
            match selection.Source with
            | Some source -> selection.Providers |> List.tryFind (fun p -> p.Source = source)
            | None -> selection.Providers |> List.tryFind (fun p -> p.FetchedAt.IsSome)
        provider
        |> Option.map (fun p ->
            { Probability = if selection.Source = Some p.Source then selection.Probability else None
              FetchedAt = p.FetchedAt |> Option.defaultValue DateTime.MinValue
              EvidenceAt = p.EvidenceAt |> Option.defaultValue (p.FetchedAt |> Option.defaultValue DateTime.MinValue)
              ExpiresAt = p.ExpiresAt
              Source = if selection.Source = Some p.Source then selection.Source else None
              IsFinal = selection.Source = Some p.Source && selection.IsFinal
              IsAssumed = selection.Source = Some p.Source && selection.IsAssumed
              Reason = if selection.Source = Some p.Source then selection.Reason else p.Reason })

    let selectSnapshot (request: SelectionRequest) (ratings: seq<TeamRating>) =
        select request ratings |> selectionToSnapshot

    let snapshotWithOptions (options: PlayoffRatingsOptions) sport season (now: DateTime) (ratings: seq<TeamRating>) =
        let team =
            ratings
            |> Seq.tryHead
            |> Option.map (fun r -> r.TeamAbbreviation)
            |> Option.defaultValue ""
        if String.IsNullOrWhiteSpace team || not (isSupported sport) then None
        else
            selectSnapshot { Sport = sport; Season = season; Team = team; Now = now; Options = options } ratings

    let snapshot sport season (now: DateTime) (ratings: seq<TeamRating>) =
        snapshotWithOptions (PlayoffRatingsOptions()) sport season now ratings

    let playoffProbability sport season now ratings =
        snapshot sport season now ratings |> Option.bind (fun s -> s.Probability)

    [<AllowNullLiteral>]
    type PlayoffSelectionCache(timeProvider: TimeProvider) =
        let evaluationTime = timeProvider.GetUtcNow().UtcDateTime
        let selections = Dictionary<string, Selection>(StringComparer.Ordinal)
        member _.EvaluationTime = evaluationTime
        member _.Select(request: SelectionRequest, ratings: seq<TeamRating>) =
            let key = $"{request.Sport}:{request.Season}:{teamKey request.Sport request.Team}:{request.Options.ModeFor(request.Sport, request.Season)}:{request.Options.MoneyPuckSelectionEnabledFor(request.Sport, request.Season)}:{evaluationTime:O}"
            match selections.TryGetValue(key) with
            | true, selection -> selection
            | _ ->
                let selection = select request ratings
                selections[key] <- selection
                selection

    let selectCached (selectionCache: PlayoffSelectionCache) (request: SelectionRequest) (ratings: seq<TeamRating>) =
        if isNull selectionCache then select request ratings else selectionCache.Select(request, ratings)
