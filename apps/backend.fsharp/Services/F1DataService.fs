namespace backend.Services.F1

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
open backend.Services.EspnF1
open backend.Services.EspnNascar

type private IncomingF1Race =
    { EspnEventId: string
      MeetingName: string
      ScheduledStart: DateTime
      Status: string
      CompletedLaps: Nullable<int> }

type F1DataService
    (espnF1: EspnF1Service,
     serviceProvider: IServiceProvider,
     logger: ILogger<F1DataService>) =

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

    static member private NormalizeMeetingName(name: string) =
        if String.IsNullOrWhiteSpace(name) then
            ""
        else
            name.ToLowerInvariant()
            |> Seq.filter Char.IsLetterOrDigit
            |> Seq.toArray
            |> String

    member this.RefreshScheduleAsync(year: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let! scoreboard = espnF1.FetchScoreboardAsync(ct = ct)

            if isNull (box scoreboard) || scoreboard.Leagues.Count = 0 then
                logger.LogWarning("No F1 scoreboard data returned")
                return 0
            else
                let calendar = scoreboard.Leagues.[0].Calendar

                if calendar.Count = 0 then
                    logger.LogWarning("No F1 calendar entries found")
                    return 0
                else
                    let parsedCalendar = ResizeArray<string * string * DateTime>()

                    for entry in calendar do
                        ct.ThrowIfCancellationRequested()

                        let eventId =
                            F1DataService.ExtractEventId(
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
                                parsedCalendar.Add(eventId, entry.Label, scheduledStart)

                    let duplicateEventIds =
                        parsedCalendar
                        |> Seq.groupBy (fun (eventId, _, _) -> eventId)
                        |> Seq.choose (fun (eventId, entries) ->
                            if Seq.length entries > 1 then Some eventId else None)
                        |> Seq.toArray

                    if duplicateEventIds.Length > 0 then
                        logger.LogError(
                            "Refusing to refresh F1 schedule for {Year}: duplicate ESPN event IDs in calendar: {EventIds}",
                            year,
                            String.Join(", ", duplicateEventIds)
                        )

                        return 0
                    else
                        let incomingRaces = ResizeArray<IncomingF1Race>()

                        for eventId, label, initialStart in parsedCalendar do
                            ct.ThrowIfCancellationRequested()
                            let mutable scheduledStart = initialStart
                            let mutable eventStatus = "scheduled"

                            let eventData =
                                scoreboard.Events
                                |> Seq.tryFind (fun e -> e.Id = eventId)

                            let! resolvedEventData =
                                task {
                                    match eventData with
                                    | Some _ -> return eventData
                                    | None ->
                                        let! dateScoreboard =
                                            espnF1.FetchScoreboardByDateAsync(scheduledStart, ct = ct)

                                        if not (isNull (box dateScoreboard)) then
                                            return
                                                dateScoreboard.Events
                                                |> Seq.tryFind (fun e -> e.Id = eventId)
                                        else
                                            return None
                                }

                            let raceCompStatus =
                                match resolvedEventData with
                                | Some ed when not (isNull ed.Competitions) ->
                                    ed.Competitions
                                    |> Seq.tryFind (fun c ->
                                        not (isNull (box c.Type))
                                        && not (isNull c.Type.Abbreviation)
                                        && c.Type.Abbreviation = "Race")
                                    |> Option.bind (fun c ->
                                        if
                                            not (isNull (box c.Status))
                                            && not (isNull (box c.Status.Type))
                                        then
                                            Some c.Status
                                        else
                                            None)
                                | _ -> None

                            let effectiveStatus =
                                match raceCompStatus with
                                | Some status -> Some status
                                | None ->
                                    match resolvedEventData with
                                    | Some ed when
                                        not (isNull (box ed.Status))
                                        && not (isNull (box ed.Status.Type))
                                        ->
                                        Some ed.Status
                                    | _ -> None

                            match effectiveStatus with
                            | Some status ->
                                eventStatus <- F1DataService.MapEspnStatus(status.Type.Name)
                            | None -> ()

                            match resolvedEventData with
                            | Some ed when not (isNull ed.Date) ->
                                let mutable eventDate = DateTime.MinValue

                                if DateTime.TryParse(ed.Date, &eventDate) then
                                    scheduledStart <- eventDate
                            | _ -> ()

                            incomingRaces.Add(
                                { EspnEventId = eventId
                                  MeetingName = label
                                  ScheduledStart = scheduledStart.ToUniversalTime()
                                  Status = eventStatus
                                  CompletedLaps =
                                    match effectiveStatus with
                                    | Some status -> status.Period
                                    | None -> Nullable<int>() }
                            )

                        use scope = serviceProvider.CreateScope()
                        let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

                        let! existingRaces =
                            db.F1Races
                                .Where(fun race -> race.Year = year)
                                .OrderBy(fun race -> race.RoundNumber)
                                .ThenBy(fun race -> race.Id)
                                .ToListAsync(ct)

                        let matches: F1Race option array =
                            Array.create incomingRaces.Count None

                        let usedExistingIds = HashSet<string>(StringComparer.Ordinal)

                        for index in 0 .. incomingRaces.Count - 1 do
                            let incoming = incomingRaces.[index]

                            let exactMatch =
                                existingRaces
                                |> Seq.tryFind (fun race ->
                                    race.EspnEventId = incoming.EspnEventId)

                            match exactMatch with
                            | Some race ->
                                matches.[index] <- Some race
                                usedExistingIds.Add(race.Id) |> ignore
                            | None -> ()

                        for index in 0 .. incomingRaces.Count - 1 do
                            if matches.[index].IsNone then
                                let incoming = incomingRaces.[index]

                                let normalizedName =
                                    F1DataService.NormalizeMeetingName(incoming.MeetingName)

                                let nameMatches =
                                    existingRaces
                                    |> Seq.filter (fun race ->
                                        not (usedExistingIds.Contains(race.Id))
                                        && F1DataService.NormalizeMeetingName(race.MeetingName) = normalizedName)
                                    |> Seq.toArray

                                if nameMatches.Length = 1 then
                                    let race = nameMatches.[0]
                                    matches.[index] <- Some race
                                    usedExistingIds.Add(race.Id) |> ignore

                        let finalRaces = ResizeArray<F1Race>()
                        let newRaces = ResizeArray<F1Race>()

                        for index in 0 .. incomingRaces.Count - 1 do
                            let incoming = incomingRaces.[index]

                            let race =
                                match matches.[index] with
                                | Some existingRace ->
                                    if existingRace.EspnEventId <> incoming.EspnEventId then
                                        logger.LogInformation(
                                            "Reconciled ESPN event ID for F1 race {RaceId} ({MeetingName}) from {OldEventId} to {NewEventId}",
                                            existingRace.Id,
                                            existingRace.MeetingName,
                                            existingRace.EspnEventId,
                                            incoming.EspnEventId
                                        )

                                        existingRace.EspnEventId <- incoming.EspnEventId

                                    existingRace
                                | None ->
                                    let newRace =
                                        F1Race(
                                            Id = incoming.EspnEventId,
                                            EspnEventId = incoming.EspnEventId,
                                            MeetingName = incoming.MeetingName,
                                            CircuitName = incoming.MeetingName,
                                            CircuitShortName =
                                                incoming.MeetingName
                                                    .ToLowerInvariant()
                                                    .Replace(" ", "_"),
                                            Country = "",
                                            Year = year,
                                            RoundNumber = index + 1,
                                            ScheduledStart = incoming.ScheduledStart,
                                            Status = incoming.Status,
                                            TotalLaps =
                                                (if
                                                     incoming.Status = "final"
                                                     && incoming.CompletedLaps.HasValue
                                                 then
                                                     incoming.CompletedLaps.Value
                                                 else
                                                     0)
                                        )

                                    newRaces.Add(newRace)
                                    newRace

                            race.MeetingName <- incoming.MeetingName
                            race.ScheduledStart <- incoming.ScheduledStart
                            race.Status <- incoming.Status

                            if incoming.CompletedLaps.HasValue then
                                race.CompletedLaps <- incoming.CompletedLaps

                                if incoming.Status = "final" then
                                    race.TotalLaps <- incoming.CompletedLaps.Value

                            finalRaces.Add(race)

                        existingRaces
                        |> Seq.filter (fun race ->
                            not (usedExistingIds.Contains(race.Id)))
                        |> Seq.iter finalRaces.Add

                        let existingRaceIds =
                            existingRaces
                            |> Seq.map (fun race -> race.Id)
                            |> HashSet<string>

                        let structuralChange =
                            newRaces.Count > 0
                            || (finalRaces
                                |> Seq.mapi (fun index race -> index + 1, race)
                                |> Seq.exists (fun (roundNumber, race) ->
                                    existingRaceIds.Contains(race.Id)
                                    && race.RoundNumber <> roundNumber))

                        if structuralChange then
                            logger.LogInformation(
                                "Reconciling structural F1 calendar change for {Year}: {ExistingCount} existing, {IncomingCount} incoming, {FinalCount} retained",
                                year,
                                existingRaces.Count,
                                incomingRaces.Count,
                                finalRaces.Count
                            )

                            use! transaction = db.Database.BeginTransactionAsync(ct)

                            let highestRound =
                                existingRaces
                                |> Seq.map (fun race -> race.RoundNumber)
                                |> Seq.append [ finalRaces.Count ]
                                |> Seq.max

                            let temporaryRoundStart =
                                highestRound + existingRaces.Count + 1

                            for index in 0 .. existingRaces.Count - 1 do
                                existingRaces.[index].RoundNumber <-
                                    temporaryRoundStart + index

                            let! _ = db.SaveChangesAsync(ct)

                            for index in 0 .. finalRaces.Count - 1 do
                                finalRaces.[index].RoundNumber <- index + 1

                            db.F1Races.AddRange(newRaces)
                            let! _ = db.SaveChangesAsync(ct)
                            do! transaction.CommitAsync(ct)
                        else
                            let! _ = db.SaveChangesAsync(ct)
                            ()

                        let count = newRaces.Count

                        let! staleRaces =
                            db.F1Races
                                .Where(fun r ->
                                    r.Year = year
                                    && r.Status = "scheduled"
                                    && r.ScheduledStart < DateTime.UtcNow)
                                .OrderBy(fun r -> r.RoundNumber)
                                .ToListAsync(ct)

                        for staleRace in staleRaces do
                            ct.ThrowIfCancellationRequested()

                            let! dateScoreboard =
                                espnF1.FetchScoreboardByDateAsync(staleRace.ScheduledStart, ct = ct)

                            if not (isNull (box dateScoreboard)) then
                                let eventMatch =
                                    dateScoreboard.Events
                                    |> Seq.tryFind (fun e -> e.Id = staleRace.EspnEventId)

                                match eventMatch with
                                | Some em ->
                                    let staleEffectiveStatus =
                                        if not (isNull em.Competitions) then
                                            em.Competitions
                                            |> Seq.tryFind (fun c ->
                                                not (isNull (box c.Type))
                                                && not (isNull c.Type.Abbreviation)
                                                && c.Type.Abbreviation = "Race")
                                            |> Option.bind (fun c ->
                                                if
                                                    not (isNull (box c.Status))
                                                    && not (isNull (box c.Status.Type))
                                                then
                                                    Some c.Status
                                                else
                                                    None)
                                        else
                                            None

                                    let staleStatus =
                                        match staleEffectiveStatus with
                                        | Some s -> Some s
                                        | None ->
                                            if
                                                not (isNull (box em.Status))
                                                && not (isNull (box em.Status.Type))
                                            then
                                                Some em.Status
                                            else
                                                None

                                    match staleStatus with
                                    | Some s ->
                                        let newStatus =
                                            F1DataService.MapEspnStatus(s.Type.Name)

                                        staleRace.Status <- newStatus

                                        if s.Period.HasValue && s.Period.Value > 0 then
                                            staleRace.CompletedLaps <- s.Period

                                            if newStatus = "final" then
                                                staleRace.TotalLaps <- s.Period.Value

                                        logger.LogInformation(
                                            "Updated stale F1 race {Name} status to {Status}",
                                            staleRace.MeetingName,
                                            newStatus
                                        )
                                    | None -> ()
                                | None -> ()

                        if staleRaces.Count > 0 then
                            let! _ = db.SaveChangesAsync(ct)
                            ()

                        if count > 0 || staleRaces.Count > 0 then
                            logger.LogInformation(
                                "Refreshed F1 schedule for {Year}: {Count} new races added, {Total} total entries, {Stale} stale statuses updated",
                                year,
                                count,
                                incomingRaces.Count,
                                staleRaces.Count
                            )
                        else
                            logger.LogDebug(
                                "F1 schedule for {Year} unchanged ({Total} total entries)",
                                year,
                                incomingRaces.Count
                            )

                        return count
        }

    member this.RefreshDriversAsync(year: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let! completedRace =
                db.F1Races
                    .Where(fun r -> r.Year = year && r.Status = "final")
                    .OrderByDescending(fun r -> r.RoundNumber)
                    .FirstOrDefaultAsync(ct)

            let mutable race = completedRace

            if isNull race then
                logger.LogWarning(
                    "No completed F1 races found for {Year}, trying nearest upcoming race",
                    year
                )

                let cutoff = DateTime.UtcNow.AddDays(-1.0)

                let! upcomingRace =
                    db.F1Races
                        .Where(fun r -> r.Year = year && r.ScheduledStart >= cutoff)
                        .OrderBy(fun r -> r.ScheduledStart)
                        .FirstOrDefaultAsync(ct)

                race <- upcomingRace

                if isNull race then
                    let! anyRace =
                        db.F1Races
                            .Where(fun r -> r.Year = year)
                            .OrderByDescending(fun r -> r.RoundNumber)
                            .FirstOrDefaultAsync(ct)

                    race <- anyRace

            if isNull race then
                logger.LogWarning("No F1 races found for {Year}", year)
                return 0
            else
                let! scoreboard = espnF1.FetchScoreboardByDateAsync(race.ScheduledStart, ct = ct)

                if isNull (box scoreboard) then
                    logger.LogWarning(
                        "Could not fetch scoreboard for race date {Date}",
                        race.ScheduledStart
                    )

                    return 0
                else
                    let eventData =
                        scoreboard.Events
                        |> Seq.tryFind (fun e -> e.Id = race.EspnEventId)

                    match eventData with
                    | None ->
                        logger.LogWarning("No event data found for race {Id}", race.Id)
                        return 0
                    | Some eventData when eventData.Competitions.Count = 0 ->
                        logger.LogWarning("No competition data found for race {Id}", race.Id)
                        return 0
                    | Some eventData ->
                        let! coreCompetitors =
                            espnF1.FetchCompetitorsAsync(race.EspnEventId, ct = ct)

                        let! existingDrivers =
                            db.F1Drivers.Where(fun d -> d.Season = year).ToListAsync(ct)

                        db.F1Drivers.RemoveRange(existingDrivers)

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
                                    | _ -> skip <- true

                                if not skip then
                                    if processedAthleteIds.Add(athleteId) then
                                        let carNumber =
                                            if isNull (box coreComp.Vehicle) then
                                                athleteId
                                            else
                                                coreComp.Vehicle.Number

                                        let teamName =
                                            if isNull (box coreComp.Vehicle) then
                                                "Unknown"
                                            else if not (isNull coreComp.Vehicle.Team) then
                                                coreComp.Vehicle.Team
                                            else if not (isNull coreComp.Vehicle.Manufacturer) then
                                                coreComp.Vehicle.Manufacturer
                                            else
                                                "Unknown"

                                        let qualPos =
                                            if coreComp.Order.HasValue then
                                                System.Nullable<int>(coreComp.Order.Value)
                                            elif coreComp.StartOrder.HasValue then
                                                System.Nullable<int>(coreComp.StartOrder.Value)
                                            else
                                                System.Nullable<int>()

                                        db.F1Drivers.Add(
                                            F1Driver(
                                                DriverNumber = carNumber,
                                                FullName =
                                                    (if not (isNull athleteFullName) then
                                                         athleteFullName
                                                     else
                                                         athleteName),
                                                Abbreviation =
                                                    (if not (isNull athleteShortName) then
                                                         athleteShortName
                                                     else
                                                         athleteName),
                                                TeamName = teamName,
                                                QualifyingPosition = qualPos,
                                                TeamColor = "#808080",
                                                HeadshotUrl = null,
                                                CountryCode = null,
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
                                        db.F1Drivers.Add(
                                            F1Driver(
                                                DriverNumber = competitor.Id,
                                                FullName = competitor.Athlete.FullName,
                                                Abbreviation = competitor.Athlete.ShortName,
                                                TeamName = "Unknown",
                                                TeamColor = "#808080",
                                                HeadshotUrl = null,
                                                CountryCode = null,
                                                Season = year
                                            )
                                        )
                                        |> ignore

                                        count <- count + 1

                        let! _ = db.SaveChangesAsync(ct)

                        logger.LogInformation(
                            "Refreshed F1 drivers for {Year}: {Count} drivers",
                            year,
                            count
                        )

                        return count
        }

    member this.RefreshChampionshipAsync(year: int, ?ct: CancellationToken) : Task<int> =
        let ct = defaultArg ct CancellationToken.None

        task {
            use scope = serviceProvider.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>()

            let! completedRaces =
                db.F1Races
                    .Where(fun r -> r.Year = year && r.Status = "final")
                    .OrderByDescending(fun r -> r.RoundNumber)
                    .ToListAsync(ct)

            if completedRaces.Count = 0 then
                logger.LogDebug(
                    "No completed F1 races for {Year}, skipping championship refresh",
                    year
                )

                return 0
            else
                let latestRound = completedRaces.First().RoundNumber

                logger.LogInformation(
                    "F1 championship standings for {Year} after round {Round}: {RaceCount} completed races",
                    year,
                    latestRound,
                    completedRaces.Count
                )

                return 0
        }
