namespace backend.Services.IndyCar

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.EntityFrameworkCore
open backend.Data
open backend.Models
open backend.Services.EspnIndyCar
open backend.Services.EspnNascar

type IndyCarDataService
    (espnIndyCar: EspnIndyCarService,
     serviceProvider: IServiceProvider,
     logger: ILogger<IndyCarDataService>) =

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
            let! scoreboard = espnIndyCar.FetchScoreboardAsync(ct = ct)

            if isNull (box scoreboard) || scoreboard.Leagues.Count = 0 then
                logger.LogWarning("No IndyCar scoreboard data returned")
                return 0
            else
                let calendar = scoreboard.Leagues.[0].Calendar

                if calendar.Count = 0 then
                    logger.LogWarning("No IndyCar calendar entries found")
                    return 0
                else
                    use scope = serviceProvider.CreateScope()
                    let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                    let mutable count = 0
                    let mutable raceNumber = 1

                    for entry in calendar do
                        ct.ThrowIfCancellationRequested()

                        let eventId =
                            IndyCarDataService.ExtractEventId(
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
                                            espnIndyCar.FetchScoreboardByDateAsync(scheduledStart, ct = ct)
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
                                        IndyCarDataService.MapEspnStatus(ed.Status.Type.Name)
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

                                let! existingRaceByEventId =
                                    db.IndyCarRaces.FirstOrDefaultAsync(
                                        (fun r -> r.EspnEventId = eventId),
                                        ct
                                    )

                                let mutable existingRace = existingRaceByEventId

                                if isNull existingRace then
                                    let! existingRaceByNumber =
                                        db.IndyCarRaces.FirstOrDefaultAsync(
                                            (fun r ->
                                                r.Year = year
                                                && r.RaceNumber = raceNumber),
                                            ct
                                        )

                                    existingRace <- existingRaceByNumber

                                if not (isNull existingRace) then
                                    if existingRace.EspnEventId <> eventId then
                                        logger.LogInformation(
                                            "Reconciled ESPN event ID for IndyCar race {RaceId} ({RaceName}) from {OldEventId} to {NewEventId}",
                                            existingRace.Id,
                                            existingRace.RaceName,
                                            existingRace.EspnEventId,
                                            eventId
                                        )

                                        existingRace.EspnEventId <- eventId

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
                                        IndyCarRace(
                                            Id = eventId,
                                            EspnEventId = eventId,
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

                                    db.IndyCarRaces.Add(race) |> ignore
                                    count <- count + 1

                                raceNumber <- raceNumber + 1

                    let! _ = db.SaveChangesAsync(ct)

                    let! staleRaces =
                        db.IndyCarRaces
                            .Where(fun r ->
                                r.Year = year
                                && r.Status = "scheduled"
                                && r.ScheduledStart < DateTime.UtcNow)
                            .OrderBy(fun r -> r.RaceNumber)
                            .ToListAsync(ct)

                    for staleRace in staleRaces do
                        ct.ThrowIfCancellationRequested()

                        let! dateScoreboard =
                            espnIndyCar.FetchScoreboardByDateAsync(staleRace.ScheduledStart, ct = ct)

                        if not (isNull (box dateScoreboard)) then
                            let eventMatch =
                                dateScoreboard.Events
                                |> Seq.tryFind (fun e -> e.Id = staleRace.EspnEventId)

                            match eventMatch with
                            | Some em when
                                not (isNull (box em.Status))
                                && not (isNull (box em.Status.Type))
                                ->
                                let newStatus =
                                    IndyCarDataService.MapEspnStatus(em.Status.Type.Name)

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
                            "Refreshed IndyCar schedule for {Year}: {Count} new races added, {Total} total entries, {Stale} stale statuses updated",
                            year,
                            count,
                            calendar.Count,
                            staleRaces.Count
                        )
                    else
                        logger.LogDebug(
                            "IndyCar schedule for {Year} unchanged ({Total} total entries)",
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
                db.IndyCarRaces
                    .Where(fun r -> r.Year = year && r.Status = "final")
                    .OrderByDescending(fun r -> r.RaceNumber)
                    .FirstOrDefaultAsync(ct)

            let mutable race = completedRace

            if isNull race then
                logger.LogWarning(
                    "No completed IndyCar races found for {Year}, trying nearest upcoming race",
                    year
                )

                let cutoff = DateTime.UtcNow.AddDays(-1.0)

                let! upcomingRace =
                    db.IndyCarRaces
                        .Where(fun r -> r.Year = year && r.ScheduledStart >= cutoff)
                        .OrderBy(fun r -> r.ScheduledStart)
                        .FirstOrDefaultAsync(ct)

                race <- upcomingRace

                if isNull race then
                    let! anyRace =
                        db.IndyCarRaces
                            .Where(fun r -> r.Year = year)
                            .OrderByDescending(fun r -> r.RaceNumber)
                            .FirstOrDefaultAsync(ct)

                    race <- anyRace

            if isNull race then
                logger.LogWarning("No IndyCar races found for {Year}", year)
                return 0
            else
                let! scoreboard = espnIndyCar.FetchScoreboardByDateAsync(race.ScheduledStart, ct = ct)

                if isNull (box scoreboard) then
                    logger.LogWarning(
                        "Could not fetch scoreboard for race date {Date}",
                        race.ScheduledStart
                    )

                    return 0
                else
                    let eventData =
                        scoreboard.Events |> Seq.tryFind (fun e -> e.Id = race.EspnEventId)

                    match eventData with
                    | None ->
                        logger.LogWarning("No event data found for race {Id}", race.Id)
                        return 0
                    | Some eventData when eventData.Competitions.Count = 0 ->
                        logger.LogWarning("No event data found for race {Id}", race.Id)
                        return 0
                    | Some eventData ->
                        let! coreCompetitors =
                            espnIndyCar.FetchCompetitorsAsync(race.EspnEventId, ct = ct)

                        let! existingDrivers =
                            db.IndyCarDrivers
                                .Where(fun d -> d.Season = year)
                                .ToDictionaryAsync((fun d -> d.EspnAthleteId), ct)

                        let mutable count = 0
                        let processedAthleteIds = HashSet<string>()

                        if
                            not (isNull (box coreCompetitors))
                            && coreCompetitors.Items.Count > 0
                        then
                            for coreComp in coreCompetitors.Items do
                                let mutable athleteId =
                                    if
                                        not (isNull (box coreComp.Athlete))
                                        && not (isNull coreComp.Athlete.Id)
                                    then
                                        coreComp.Athlete.Id
                                    else
                                        coreComp.Id

                                let mutable athleteName =
                                    if not (isNull (box coreComp.Athlete)) then
                                        coreComp.Athlete.DisplayName
                                    else
                                        null

                                let mutable athleteFullName =
                                    if not (isNull (box coreComp.Athlete)) then
                                        coreComp.Athlete.FullName
                                    else
                                        null

                                let mutable athleteShortName =
                                    if not (isNull (box coreComp.Athlete)) then
                                        coreComp.Athlete.ShortName
                                    else
                                        null

                                let mutable flagHref =
                                    if
                                        not (isNull (box coreComp.Athlete))
                                        && not (isNull (box coreComp.Athlete.Flag))
                                    then
                                        coreComp.Athlete.Flag.Href
                                    else
                                        null

                                let mutable skip = false

                                if String.IsNullOrEmpty(athleteName) then
                                    let competitors = eventData.Competitions.[0].Competitors

                                    let scoreboardComp =
                                        competitors
                                        |> Seq.tryFind (fun c -> c.Id = coreComp.Id)

                                    match scoreboardComp with
                                    | Some sc when not (isNull (box sc.Athlete)) ->
                                        athleteId <-
                                            (if not (isNull sc.Id) then sc.Id else athleteId)

                                        athleteName <- sc.Athlete.DisplayName
                                        athleteFullName <- sc.Athlete.FullName
                                        athleteShortName <- sc.Athlete.ShortName

                                        flagHref <-
                                            (if isNull (box sc.Athlete.Flag) then
                                                 null
                                             else
                                                 sc.Athlete.Flag.Href)
                                    | _ -> skip <- true  

                                if not skip then
                                    if processedAthleteIds.Add(athleteId) then
                                        let mutable existing: IndyCarDriver = null

                                        if existingDrivers.TryGetValue(athleteId, &existing) then
                                            existing.FullName <-
                                                (if not (isNull athleteFullName) then
                                                     athleteFullName
                                                 else
                                                     athleteName)

                                            existing.DisplayName <- athleteName
                                            existing.ShortName <-
                                                (if not (isNull athleteShortName) then
                                                     athleteShortName
                                                 else
                                                     athleteName)

                                            existing.CountryFlagHref <- flagHref

                                            existing.CarNumber <-
                                                (if isNull (box coreComp.Vehicle) then
                                                     null
                                                 else
                                                     coreComp.Vehicle.Number)

                                            existing.TeamName <-
                                                (if isNull (box coreComp.Vehicle) then
                                                     null
                                                 else
                                                     coreComp.Vehicle.Team)
                                        else
                                            db.IndyCarDrivers.Add(
                                                IndyCarDriver(
                                                    EspnAthleteId = athleteId,
                                                    FullName =
                                                        (if not (isNull athleteFullName) then
                                                             athleteFullName
                                                         else
                                                             athleteName),
                                                    DisplayName = athleteName,
                                                    ShortName =
                                                        (if not (isNull athleteShortName) then
                                                             athleteShortName
                                                         else
                                                             athleteName),
                                                    CountryFlagHref = flagHref,
                                                    CarNumber =
                                                        (if isNull (box coreComp.Vehicle) then
                                                             null
                                                         else
                                                             coreComp.Vehicle.Number),
                                                    TeamName =
                                                        (if isNull (box coreComp.Vehicle) then
                                                             null
                                                         else
                                                             coreComp.Vehicle.Team),
                                                    Season = year
                                                )
                                            )
                                            |> ignore

                                        count <- count + 1
                        else
                            let competitors = eventData.Competitions.[0].Competitors

                            for competitor in competitors do
                                if not (isNull (box competitor.Athlete)) then
                                    if processedAthleteIds.Add(competitor.Id) then
                                        let mutable existing: IndyCarDriver = null

                                        if
                                            existingDrivers.TryGetValue(
                                                competitor.Id,
                                                &existing
                                            )
                                        then
                                            existing.FullName <- competitor.Athlete.FullName
                                            existing.DisplayName <- competitor.Athlete.DisplayName
                                            existing.ShortName <- competitor.Athlete.ShortName

                                            existing.CountryFlagHref <-
                                                (if isNull (box competitor.Athlete.Flag) then
                                                     null
                                                 else
                                                     competitor.Athlete.Flag.Href)
                                        else
                                            db.IndyCarDrivers.Add(
                                                IndyCarDriver(
                                                    EspnAthleteId = competitor.Id,
                                                    FullName = competitor.Athlete.FullName,
                                                    DisplayName = competitor.Athlete.DisplayName,
                                                    ShortName = competitor.Athlete.ShortName,
                                                    CountryFlagHref =
                                                        (if
                                                             isNull (box competitor.Athlete.Flag)
                                                         then
                                                             null
                                                         else
                                                             competitor.Athlete.Flag.Href),
                                                    CarNumber = null,
                                                    TeamName = null,
                                                    Season = year
                                                )
                                            )
                                            |> ignore

                                        count <- count + 1

                        let! _ = db.SaveChangesAsync(ct)

                        logger.LogInformation(
                            "Refreshed IndyCar drivers for {Year}: {Count} drivers",
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
                db.IndyCarRaces
                    .Where(fun r -> r.Year = year && r.Status = "final")
                    .Select(fun r -> r.Id)
                    .ToListAsync(ct)

            let! racesWithResults =
                db.IndyCarRaceResults
                    .Where(fun r -> completedRaceIds.Contains(r.RaceId))
                    .Select(fun r -> r.RaceId)
                    .Distinct()
                    .ToListAsync(ct)

            let! racesNeedingResults =
                db.IndyCarRaces
                    .Where(fun r ->
                        completedRaceIds.Contains(r.Id)
                        && not (racesWithResults.Contains(r.Id)))
                    .OrderBy(fun r -> r.RaceNumber)
                    .ToListAsync(ct)

            if racesNeedingResults.Count = 0 then
                logger.LogDebug(
                    "All completed IndyCar races for {Year} already have results",
                    year
                )

                return 0
            else
                let mutable totalResultsAdded = 0

                for race in racesNeedingResults do
                    ct.ThrowIfCancellationRequested()

                    let! scoreboard = espnIndyCar.FetchScoreboardByDateAsync(race.ScheduledStart, ct = ct)

                    if not (isNull (box scoreboard)) then
                        let eventData =
                            scoreboard.Events
                            |> Seq.tryFind (fun e -> e.Id = race.EspnEventId)

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
                                        db.IndyCarRaceResults.Add(
                                            IndyCarRaceResult(
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
                                    espnIndyCar.FetchCompetitorsAsync(race.EspnEventId, ct = ct)

                                if
                                    not (isNull (box coreCompetitors))
                                    && coreCompetitors.Items.Count > 0
                                then
                                    let! results =
                                        db.IndyCarRaceResults
                                            .Where(fun r -> r.RaceId = race.Id)
                                            .ToListAsync(ct)

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

                                            let result =
                                                results
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
                    "Refreshed IndyCar race results for {Year}: {Total} results across {Races} races",
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
                db.IndyCarRaces
                    .Where(fun r ->
                        r.Year = year
                        && (r.Status = "scheduled" || r.Status = "in_progress")
                        && r.ScheduledStart <= cutoff)
                    .OrderBy(fun r -> r.RaceNumber)
                    .ToListAsync(ct)

            if races.Count > 0 then
                for race in races do
                    ct.ThrowIfCancellationRequested()

                    let! coreCompetitors =
                        espnIndyCar.FetchCompetitorsAsync(race.EspnEventId, ct = ct)

                    if
                        not (isNull (box coreCompetitors))
                        && coreCompetitors.Items.Count > 0
                    then
                        let hasStartOrders =
                            coreCompetitors.Items
                            |> Seq.exists (fun c -> c.StartOrder.HasValue)

                        if hasStartOrders then
                            let! existingResults =
                                db.IndyCarRaceResults
                                    .Where(fun r -> r.RaceId = race.Id)
                                    .ToListAsync(ct)

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
                                    | None ->
                                        db.IndyCarRaceResults.Add(
                                            IndyCarRaceResult(
                                                RaceId = race.Id,
                                                EspnAthleteId = athleteId,
                                                DisplayName =
                                                    (if
                                                         not (isNull (box coreComp.Athlete))
                                                         && not (
                                                             isNull
                                                                 coreComp.Athlete.DisplayName
                                                         )
                                                     then
                                                         coreComp.Athlete.DisplayName
                                                     else
                                                         athleteId),
                                                ShortName =
                                                    (if
                                                         not (isNull (box coreComp.Athlete))
                                                         && not (
                                                             isNull
                                                                 coreComp.Athlete.ShortName
                                                         )
                                                     then
                                                         coreComp.Athlete.ShortName
                                                     else
                                                         athleteId),
                                                StartingPosition = coreComp.StartOrder,
                                                Winner = false,
                                                CountryFlagHref =
                                                    (if
                                                         not (isNull (box coreComp.Athlete))
                                                         && not (
                                                             isNull
                                                                 (box coreComp.Athlete.Flag)
                                                         )
                                                     then
                                                         coreComp.Athlete.Flag.Href
                                                     else
                                                         null),
                                                CarNumber =
                                                    (if isNull (box coreComp.Vehicle) then
                                                         null
                                                     else
                                                         coreComp.Vehicle.Number)
                                            )
                                        )
                                        |> ignore

                            let! _ = db.SaveChangesAsync(ct)

                            logger.LogInformation(
                                "Refreshed starting grid for IndyCar race {RaceName}: {Count} positions",
                                race.RaceName,
                                coreCompetitors.Items
                                |> Seq.filter (fun c -> c.StartOrder.HasValue)
                                |> Seq.length
                            )
        }
        :> Task
