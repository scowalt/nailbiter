namespace backend.Services.Nascar

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.EntityFrameworkCore
open backend.Data
open backend.Models
open backend.Services.EspnNascar

type NascarDataService
    (espnNascar: EspnNascarService,
     serviceProvider: IServiceProvider,
     logger: ILogger<NascarDataService>) =

    static member private ExtractEventId(refUrl: string) =
        if String.IsNullOrEmpty(refUrl) then
            null
        else
            let segments = refUrl.Split('/')
            let mutable result: string = null

            for i in 0 .. segments.Length - 1 do
                if isNull result && segments.[i] = "events" && i + 1 < segments.Length then
                    let id = segments.[i + 1]
                    let qIdx = id.IndexOf('?')
                    result <- if qIdx >= 0 then id.[..qIdx - 1] else id

            result

    static member private MapEspnStatus(espnStatus: string) =
        match espnStatus with
        | "STATUS_FINAL" -> "final"
        | "STATUS_IN_PROGRESS" -> "in_progress"
        | "STATUS_CANCELED" -> "canceled"
        | _ -> "scheduled"

    member this.RefreshScheduleAsync(year: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let! scoreboard = espnNascar.FetchScoreboardAsync()

            if isNull (box scoreboard) || scoreboard.Leagues.Count = 0 then
                logger.LogWarning("No NASCAR scoreboard data returned")
                return 0
            else
                let calendar = scoreboard.Leagues.[0].Calendar

                if calendar.Count = 0 then
                    logger.LogWarning("No NASCAR calendar entries found")
                    return 0
                else
                    use scope = serviceProvider.CreateScope()
                    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                    let mutable count = 0
                    let mutable raceNumber = 1

                    for entry in calendar do
                        ct.ThrowIfCancellationRequested()

                        let eventId =
                            NascarDataService.ExtractEventId(
                                if isNull (box entry.Event) then
                                    null
                                else
                                    entry.Event.Ref
                            )

                        if isNull eventId then
                            logger.LogDebug(
                                "No event ref for calendar entry {Label}, skipping",
                                entry.Label
                            )
                        else
                            let mutable scheduledStart = DateTime.MinValue
                            let parsed = DateTime.TryParse(entry.StartDate, &scheduledStart)

                            if not parsed then
                                logger.LogDebug(
                                    "Could not parse start date for {Label}, skipping",
                                    entry.Label
                                )
                            else
                                let mutable eventStatus = "scheduled"

                                let eventData =
                                    scoreboard.Events
                                    |> Seq.tryFind (fun e -> e.Id = eventId)

                                let! resolvedEventData = task {
                                    match eventData with
                                    | Some _ -> return eventData
                                    | None ->
                                        let! dateScoreboard =
                                            espnNascar.FetchScoreboardByDateAsync(scheduledStart)
                                        if not (isNull (box dateScoreboard)) then
                                            return dateScoreboard.Events
                                                   |> Seq.tryFind (fun e -> e.Id = eventId)
                                        else
                                            return None
                                }

                                match resolvedEventData with
                                | Some ed when
                                    not (isNull (box ed.Status))
                                    && not (isNull (box ed.Status.Type))
                                    ->
                                    eventStatus <-
                                        NascarDataService.MapEspnStatus(ed.Status.Type.Name)
                                | _ -> ()

                                match resolvedEventData with
                                | Some ed when not (isNull ed.Date) ->
                                    let mutable eventDate = DateTime.MinValue
                                    if DateTime.TryParse(ed.Date, &eventDate) then
                                        scheduledStart <- eventDate
                                | _ -> ()

                                let completedLaps =
                                    match resolvedEventData with
                                    | Some ed when not (isNull (box ed.Status)) -> ed.Status.Period
                                    | _ -> Nullable<int>()

                                let! existingRace =
                                    db.NascarRaces.FirstOrDefaultAsync(
                                        (fun r -> r.Id = eventId),
                                        ct
                                    )

                                if not (isNull existingRace) then
                                    existingRace.RaceName <- entry.Label
                                    existingRace.RaceNumber <- raceNumber
                                    existingRace.ScheduledStart <- scheduledStart.ToUniversalTime()
                                    existingRace.Status <- eventStatus

                                    if completedLaps.HasValue then
                                        existingRace.CompletedLaps <- completedLaps

                                        if eventStatus = "final" then
                                            existingRace.TotalLaps <- completedLaps.Value
                                else
                                    let race =
                                        NascarRace(
                                            Id = eventId,
                                            RaceName = entry.Label,
                                            Year = year,
                                            RaceNumber = raceNumber,
                                            ScheduledStart = scheduledStart.ToUniversalTime(),
                                            Status = eventStatus,
                                            TotalLaps =
                                                (if eventStatus = "final" && completedLaps.HasValue then
                                                     completedLaps.Value
                                                 else
                                                     0)
                                        )

                                    if completedLaps.HasValue then
                                        race.CompletedLaps <- completedLaps

                                    db.NascarRaces.Add(race) |> ignore
                                    count <- count + 1

                                raceNumber <- raceNumber + 1

                    let! _ = db.SaveChangesAsync(ct)

                    let! staleRaces =
                        db.NascarRaces
                            .Where(fun r ->
                                r.Year = year
                                && r.Status = "scheduled"
                                && r.ScheduledStart < DateTime.UtcNow)
                            .OrderBy(fun r -> r.RaceNumber)
                            .ToListAsync(ct)

                    for staleRace in staleRaces do
                        ct.ThrowIfCancellationRequested()

                        let! dateScoreboard =
                            espnNascar.FetchScoreboardByDateAsync(staleRace.ScheduledStart)

                        if not (isNull (box dateScoreboard)) then
                            let eventMatch =
                                dateScoreboard.Events
                                |> Seq.tryFind (fun e -> e.Id = staleRace.Id)

                            match eventMatch with
                            | Some em when
                                not (isNull (box em.Status))
                                && not (isNull (box em.Status.Type))
                                ->
                                let newStatus =
                                    NascarDataService.MapEspnStatus(em.Status.Type.Name)

                                staleRace.Status <- newStatus

                                if em.Status.Period.HasValue then
                                    staleRace.CompletedLaps <- em.Status.Period

                                    if newStatus = "final" then
                                        staleRace.TotalLaps <- em.Status.Period.Value

                                logger.LogInformation(
                                    "Updated stale race {Name} status to {Status}",
                                    staleRace.RaceName,
                                    newStatus
                                )
                            | _ -> ()

                    if staleRaces.Count > 0 then
                        let! _ = db.SaveChangesAsync(ct)
                        ()

                    if count > 0 || staleRaces.Count > 0 then
                        logger.LogInformation(
                            "Refreshed NASCAR schedule for {Year}: {Count} new races added, {Total} total entries, {Stale} stale statuses updated",
                            year,
                            count,
                            calendar.Count,
                            staleRaces.Count
                        )
                    else
                        logger.LogDebug(
                            "NASCAR schedule for {Year} unchanged ({Total} total entries)",
                            year,
                            calendar.Count
                        )

                    return count
        }

    member this.RefreshDriversAsync(year: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let! completedRace =
                db.NascarRaces
                    .Where(fun r -> r.Year = year && r.Status = "final")
                    .OrderByDescending(fun r -> r.RaceNumber)
                    .FirstOrDefaultAsync(ct)

            let mutable race = completedRace

            if isNull race then
                logger.LogWarning(
                    "No completed NASCAR races found for {Year}, trying latest race",
                    year
                )

                let! latestRace =
                    db.NascarRaces
                        .Where(fun r -> r.Year = year)
                        .OrderByDescending(fun r -> r.RaceNumber)
                        .FirstOrDefaultAsync(ct)

                race <- latestRace

            if isNull race then
                logger.LogWarning("No NASCAR races found for {Year}", year)
                return 0
            else
                let! scoreboard = espnNascar.FetchScoreboardByDateAsync(race.ScheduledStart)

                if isNull (box scoreboard) then
                    logger.LogWarning(
                        "Could not fetch scoreboard for race date {Date}",
                        race.ScheduledStart
                    )

                    return 0
                else
                    let eventData =
                        scoreboard.Events |> Seq.tryFind (fun e -> e.Id = race.Id)

                    match eventData with
                    | None ->
                        logger.LogWarning("No event data found for race {Id}", race.Id)
                        return 0
                    | Some eventData when eventData.Competitions.Count = 0 ->
                        logger.LogWarning("No event data found for race {Id}", race.Id)
                        return 0
                    | Some eventData ->
                        let competitors = eventData.Competitions.[0].Competitors

                        if competitors.Count = 0 then
                            logger.LogWarning("No competitors found for race {Id}", race.Id)
                            return 0
                        else
                            let! existingDrivers =
                                db.NascarDrivers.Where(fun d -> d.Season = year).ToListAsync(ct)

                            db.NascarDrivers.RemoveRange(existingDrivers)

                            let mutable count = 0

                            for competitor in competitors do
                                if not (isNull (box competitor.Athlete)) then
                                    db.NascarDrivers.Add(
                                        NascarDriver(
                                            EspnAthleteId = competitor.Id,
                                            FullName = competitor.Athlete.FullName,
                                            DisplayName = competitor.Athlete.DisplayName,
                                            ShortName = competitor.Athlete.ShortName,
                                            CountryFlagHref =
                                                (if isNull (box competitor.Athlete.Flag) then
                                                     null
                                                 else
                                                     competitor.Athlete.Flag.Href),
                                            CarNumber =
                                                (if isNull (box competitor.Vehicle) then
                                                     null
                                                 else
                                                     competitor.Vehicle.Number),
                                            Manufacturer =
                                                (if isNull (box competitor.Vehicle) then
                                                     null
                                                 else
                                                     competitor.Vehicle.Manufacturer),
                                            TeamName =
                                                (if isNull (box competitor.Vehicle) then
                                                     null
                                                 else
                                                     competitor.Vehicle.Team),
                                            Season = year
                                        )
                                    )
                                    |> ignore

                                    count <- count + 1

                            if competitors |> Seq.forall (fun c -> isNull (box c.Vehicle)) then
                                let! coreCompetitors = espnNascar.FetchCompetitorsAsync(race.Id)

                                if not (isNull (box coreCompetitors)) then
                                    let! _ = db.SaveChangesAsync(ct)

                                    let! savedDrivers =
                                        db.NascarDrivers
                                            .Where(fun d -> d.Season = year)
                                            .ToListAsync(ct)

                                    for coreComp in coreCompetitors.Items do
                                        let athleteId =
                                            if
                                                not (isNull (box coreComp.Athlete))
                                                && not (isNull coreComp.Athlete.Id)
                                            then
                                                coreComp.Athlete.Id
                                            else
                                                coreComp.Id

                                        let driver =
                                            savedDrivers
                                            |> Seq.tryFind (fun d ->
                                                d.EspnAthleteId = athleteId)

                                        match driver with
                                        | Some driver when not (isNull (box coreComp.Vehicle)) ->
                                            driver.CarNumber <- coreComp.Vehicle.Number
                                            driver.Manufacturer <- coreComp.Vehicle.Manufacturer
                                            driver.TeamName <- coreComp.Vehicle.Team
                                        | _ -> ()

                            let! _ = db.SaveChangesAsync(ct)

                            logger.LogInformation(
                                "Refreshed NASCAR drivers for {Year}: {Count} drivers",
                                year,
                                count
                            )

                            return count
        }

    member this.RefreshRaceResultsAsync(year: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let! completedRaceIds =
                db.NascarRaces
                    .Where(fun r -> r.Year = year && r.Status = "final")
                    .Select(fun r -> r.Id)
                    .ToListAsync(ct)

            let! racesWithResults =
                db.NascarRaceResults
                    .Where(fun r -> completedRaceIds.Contains(r.RaceId))
                    .Select(fun r -> r.RaceId)
                    .Distinct()
                    .ToListAsync(ct)

            let! racesNeedingResults =
                db.NascarRaces
                    .Where(fun r ->
                        completedRaceIds.Contains(r.Id)
                        && not (racesWithResults.Contains(r.Id)))
                    .OrderBy(fun r -> r.RaceNumber)
                    .ToListAsync(ct)

            if racesNeedingResults.Count = 0 then
                logger.LogDebug(
                    "All completed NASCAR races for {Year} already have results",
                    year
                )

                return 0
            else
                let mutable totalResultsAdded = 0

                for race in racesNeedingResults do
                    ct.ThrowIfCancellationRequested()

                    let! scoreboard = espnNascar.FetchScoreboardByDateAsync(race.ScheduledStart)

                    if not (isNull (box scoreboard)) then
                        let eventData =
                            scoreboard.Events |> Seq.tryFind (fun e -> e.Id = race.Id)

                        match eventData with
                        | Some eventData when
                            not (isNull eventData.Competitions)
                            && eventData.Competitions.Count > 0
                            ->
                            let competitors = eventData.Competitions.[0].Competitors

                            if competitors.Count > 0 then
                                if
                                    race.TotalLaps = 0
                                    && not (isNull (box eventData.Status))
                                    && eventData.Status.Period.HasValue
                                    && eventData.Status.Period.Value > 0
                                then
                                    race.TotalLaps <- eventData.Status.Period.Value
                                    race.CompletedLaps <- eventData.Status.Period

                                for competitor in competitors do
                                    if not (isNull (box competitor.Athlete)) then
                                        db.NascarRaceResults.Add(
                                            NascarRaceResult(
                                                RaceId = race.Id,
                                                EspnAthleteId = competitor.Id,
                                                DisplayName =
                                                    competitor.Athlete.DisplayName,
                                                ShortName = competitor.Athlete.ShortName,
                                                FinishPosition = competitor.Order,
                                                Winner =
                                                    (if competitor.Winner.HasValue then
                                                         competitor.Winner.Value
                                                     else
                                                         false),
                                                CountryFlagHref =
                                                    (if isNull (box competitor.Athlete.Flag) then
                                                         null
                                                     else
                                                         competitor.Athlete.Flag.Href),
                                                CarNumber =
                                                    (if isNull (box competitor.Vehicle) then
                                                         null
                                                     else
                                                         competitor.Vehicle.Number)
                                            )
                                        )
                                        |> ignore

                                        totalResultsAdded <- totalResultsAdded + 1

                                let! coreCompetitors =
                                    espnNascar.FetchCompetitorsAsync(race.Id)

                                if not (isNull (box coreCompetitors)) then
                                    let! _ = db.SaveChangesAsync(ct)

                                    let! savedResults =
                                        db.NascarRaceResults
                                            .Where(fun r -> r.RaceId = race.Id)
                                            .ToListAsync(ct)

                                    for coreComp in coreCompetitors.Items do
                                        let athleteId =
                                            if
                                                not (isNull (box coreComp.Athlete))
                                                && not (isNull coreComp.Athlete.Id)
                                            then
                                                coreComp.Athlete.Id
                                            else
                                                coreComp.Id

                                        let result =
                                            savedResults
                                            |> Seq.tryFind (fun r ->
                                                r.EspnAthleteId = athleteId)

                                        match result with
                                        | Some result when coreComp.StartOrder.HasValue ->
                                            result.StartingPosition <- coreComp.StartOrder
                                        | _ -> ()

                                logger.LogInformation(
                                    "Added {Count} results for race {RaceName}",
                                    competitors.Count,
                                    race.RaceName
                                )
                        | _ -> ()

                let! _ = db.SaveChangesAsync(ct)

                logger.LogInformation(
                    "Refreshed NASCAR race results for {Year}: {Total} results across {Races} races",
                    year,
                    totalResultsAdded,
                    racesNeedingResults.Count
                )

                return totalResultsAdded
        }

    member this.RefreshStartingGridAsync(year: int, ?ct: CancellationToken) : Task =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let cutoff = DateTime.UtcNow.AddDays(7.0)

            let! races =
                db.NascarRaces
                    .Where(fun r ->
                        r.Year = year
                        && (r.Status = "scheduled" || r.Status = "in_progress")
                        && r.ScheduledStart <= cutoff)
                    .OrderBy(fun r -> r.RaceNumber)
                    .ToListAsync(ct)

            if races.Count > 0 then
                for race in races do
                    ct.ThrowIfCancellationRequested()

                    let! coreCompetitors = espnNascar.FetchCompetitorsAsync(race.Id)

                    if
                        not (isNull (box coreCompetitors))
                        && coreCompetitors.Items.Count > 0
                    then
                        let hasStartOrders =
                            coreCompetitors.Items
                            |> Seq.exists (fun c -> c.StartOrder.HasValue)

                        if hasStartOrders then
                            let! existingResults =
                                db.NascarRaceResults
                                    .Where(fun r -> r.RaceId = race.Id)
                                    .ToListAsync(ct)

                            let! driverLookup =
                                db.NascarDrivers
                                    .Where(fun d -> d.Season = year)
                                    .ToDictionaryAsync((fun d -> d.EspnAthleteId), ct)

                            for coreComp in coreCompetitors.Items do
                                if coreComp.StartOrder.HasValue then
                                    let athleteId =
                                        if
                                            not (isNull (box coreComp.Athlete))
                                            && not (isNull coreComp.Athlete.Id)
                                        then
                                            coreComp.Athlete.Id
                                        else
                                            coreComp.Id

                                    let existing =
                                        existingResults
                                        |> Seq.tryFind (fun r ->
                                            r.EspnAthleteId = athleteId)

                                    match existing with
                                    | Some existing ->
                                        existing.StartingPosition <- coreComp.StartOrder
                                        let allDigits = existing.DisplayName |> Seq.forall System.Char.IsDigit
                                        if allDigits then
                                            match driverLookup.TryGetValue(athleteId) with
                                            | true, driver ->
                                                existing.DisplayName <- driver.DisplayName
                                                existing.ShortName <- driver.ShortName
                                                if isNull existing.CountryFlagHref then
                                                    existing.CountryFlagHref <- driver.CountryFlagHref
                                                if isNull existing.CarNumber then
                                                    existing.CarNumber <- driver.CarNumber
                                            | _ -> ()
                                    | None ->
                                        let mutable displayName = athleteId
                                        let mutable shortName = athleteId
                                        let mutable flagHref: string = null
                                        let mutable carNumber: string = null

                                        match driverLookup.TryGetValue(athleteId) with
                                        | true, driver ->
                                            displayName <- driver.DisplayName
                                            shortName <- driver.ShortName
                                            flagHref <- driver.CountryFlagHref
                                            if isNull carNumber then
                                                carNumber <- driver.CarNumber
                                        | _ -> ()

                                        if displayName = athleteId
                                           && not (isNull (box coreComp.Athlete))
                                           && not (isNull coreComp.Athlete.DisplayName)
                                        then
                                            displayName <- coreComp.Athlete.DisplayName
                                            shortName <- coreComp.Athlete.ShortName
                                            if not (isNull (box coreComp.Athlete.Flag)) then
                                                flagHref <- coreComp.Athlete.Flag.Href

                                        if not (isNull (box coreComp.Vehicle)) then
                                            carNumber <- coreComp.Vehicle.Number

                                        db.NascarRaceResults.Add(
                                            NascarRaceResult(
                                                RaceId = race.Id,
                                                EspnAthleteId = athleteId,
                                                DisplayName = displayName,
                                                ShortName = shortName,
                                                StartingPosition = coreComp.StartOrder,
                                                Winner = false,
                                                CountryFlagHref = flagHref,
                                                CarNumber = carNumber
                                            )
                                        )
                                        |> ignore

                            let! _ = db.SaveChangesAsync(ct)

                            logger.LogInformation(
                                "Refreshed starting grid for {RaceName}: {Count} positions",
                                race.RaceName,
                                coreCompetitors.Items
                                |> Seq.filter (fun c -> c.StartOrder.HasValue)
                                |> Seq.length
                            )
        }
        :> Task
