namespace backend.Services

open System
open System.Collections.Concurrent
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type MlbEspnStatus = { Name: string; State: string; Completed: bool; Detail: string; Interrupted: bool; Period: int option }

module MlbEspnScoreboard =
    let private property (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) && value.ValueKind <> JsonValueKind.Null then
            Some value
        else None

    let private text element name =
        property element name
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
        |> Option.map _.GetString()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private whole (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetDouble() with
            | true, number when number = Math.Round number && abs number <= float Int32.MaxValue -> Some(int number)
            | _ -> None
        | JsonValueKind.String ->
            match Int32.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, number -> Some number
            | _ -> None
        | _ -> None

    let private items element name =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.Array -> Some(List.ofSeq (value.EnumerateArray()))
        | _ -> None

    let private competition (event: JsonElement) =
        match items event "competitions" with
        | Some [ competition ] -> Some competition
        | _ -> None

    let private interrupted (texts: string list) =
        texts
        |> List.exists (fun text ->
            [ "postpon"; "suspend"; "cancel"; "delay"; "abandon" ]
            |> List.exists (fun word -> text.Contains(word, StringComparison.OrdinalIgnoreCase)))

    let private level (status: JsonElement) =
        property status "type"
        |> Option.map (fun kind ->
            {| Name = text kind "name" |> Option.toObj
               State = text kind "state" |> Option.toObj
               Completed = property kind "completed" |> Option.exists (fun value -> value.ValueKind = JsonValueKind.True)
               Detail = text kind "detail" |> Option.orElse (text status "detail")
               Texts = [ kind; status ] |> List.collect (fun element -> [ "description"; "detail"; "shortDetail" ] |> List.choose (text element))
               Period = property status "period" |> Option.bind whole |})

    let status (event: JsonElement) =
        let contest = competition event |> Option.bind (fun competition -> property competition "status")
        let levels = [ contest; property event "status" ] |> List.choose id |> List.map level
        match levels with
        | [] -> None
        | _ when levels |> List.exists Option.isNone -> None
        | _ ->
            let levels = List.choose id levels
            let first = List.head levels
            if levels |> List.forall (fun other -> (other.Name, other.State, other.Completed) = (first.Name, first.State, first.Completed)) then
                Some
                    { Name = first.Name
                      State = first.State
                      Completed = first.Completed
                      Detail = levels |> List.tryPick _.Detail |> Option.toObj
                      Interrupted = levels |> List.collect _.Texts |> interrupted
                      Period = levels |> List.tryPick _.Period }
            else None

    let frameState (status: MlbEspnStatus) =
        match status.Name, status.State with
        | "STATUS_SCHEDULED", "pre" -> Some "scheduled"
        | "STATUS_IN_PROGRESS", "in" -> Some "in_progress"
        | ("STATUS_RAIN_DELAY" | "STATUS_DELAYED"), _ -> Some "delayed"
        | "STATUS_SUSPENDED", _ -> Some "suspended"
        | "STATUS_POSTPONED", _ -> Some "postponed"
        | "STATUS_CANCELED", _ -> Some "canceled"
        | "STATUS_FINAL", "post" when status.Completed && not status.Interrupted -> Some "final"
        | _ -> None

    let gameStatus (existing: string) (event: JsonElement) =
        let kept = if isNull existing then "unknown" else existing
        match status event with
        | None -> kept
        | Some status ->
            match frameState status, status.State with
            | Some ("delayed" | "suspended"), "pre" -> "scheduled"
            | Some ("delayed" | "suspended"), _ -> "in_progress"
            | Some state, _ -> state
            | None, "pre" -> "scheduled"
            | None, "in" -> "in_progress"
            | None, _ -> kept

    let eventId (event: JsonElement) = text event "id"

    let events (board: JsonElement) : Map<string, JsonElement> =
        items board "events"
        |> Option.defaultValue []
        |> List.choose (fun event -> eventId event |> Option.map (fun id -> id, event))
        |> List.groupBy fst
        |> List.choose (function
            | id, [ _, event ] -> Some(id, event)
            | _ -> None)
        |> Map.ofList

    let private line (side: string) (competitor: JsonElement) : Result<MlbTeamLineReport, string> =
        let malformed field = Error $"malformed {side} {field}"
        let optional field =
            match property competitor field with
            | None -> Ok(Nullable())
            | Some value ->
                match whole value with
                | Some number -> Ok(Nullable number)
                | None -> malformed field
        let innings =
            match property competitor "linescores" with
            | None -> Ok null
            | Some scores when scores.ValueKind = JsonValueKind.Array ->
                let cells =
                    scores.EnumerateArray()
                    |> Seq.mapi (fun index score ->
                        let inOrder = property score "period" |> Option.forall (fun period -> whole period = Some(index + 1))
                        match property score "value" with
                        | _ when not inOrder -> None
                        | None -> Some(Nullable())
                        | Some value -> whole value |> Option.map Nullable)
                    |> Array.ofSeq
                if Array.forall Option.isSome cells then Ok(Array.map Option.get cells) else malformed "linescore"
            | Some _ -> malformed "linescore"
        match optional "score", optional "hits", optional "errors", innings with
        | Ok runs, Ok hits, Ok errors, Ok innings -> Ok { Runs = runs; Hits = hits; Errors = errors; Innings = innings }
        | Error fault, _, _, _
        | _, Error fault, _, _
        | _, _, Error fault, _
        | _, _, _, Error fault -> Error fault

    let private team (competitor: JsonElement) =
        match text competitor "id", property competitor "team" |> Option.bind (fun team -> text team "id") with
        | Some id, Some teamId when id <> teamId -> Error "competitor identity"
        | id, _ -> Ok(Option.toObj id)

    let report (gameId: string) (event: JsonElement) (observedAt: DateTime) (revision: Nullable<DateTimeOffset>)
        : Result<MlbScoreboardReport, string> =
        match competition event, status event with
        | None, _ -> Error "competition"
        | _, None -> Error "status"
        | Some competition, Some status ->
        match frameState status with
        | None -> Error $"status {status.Name}/{status.State}/{status.Completed}"
        | Some state ->
        let competitors = items competition "competitors" |> Option.defaultValue []
        let side name = competitors |> List.filter (fun competitor -> text competitor "homeAway" = Some name)
        match side "home", side "away" with
        | [ home ], [ away ] when competitors.Length = 2 ->
            match team home, team away, line "home" home, line "away" away with
            | Ok homeTeamId, Ok awayTeamId, Ok homeLine, Ok awayLine ->
                Ok
                    { GameId = eventId event |> Option.toObj
                      CompetitionId = text competition "id" |> Option.toObj
                      Provider = "espn"
                      Provenance = "live"
                      HomeTeamId = homeTeamId
                      AwayTeamId = awayTeamId
                      State = state
                      Detail = status.Detail
                      ObservedAt = Nullable(DateTimeOffset(DateTime.SpecifyKind(observedAt, DateTimeKind.Utc)))
                      SourceRevisionAt = revision
                      Inning = status.Period |> Option.filter (fun period -> period > 0) |> Option.toNullable
                      Half = null
                      Count = Unchecked.defaultof<_>
                      Bases = Unchecked.defaultof<_>
                      Matchup = Unchecked.defaultof<_>
                      Away = awayLine
                      Home = homeLine }
            | Error fault, _, _, _
            | _, Error fault, _, _
            | _, _, Error fault, _
            | _, _, _, Error fault -> Error fault
        | _ -> Error "competitors"
        |> Result.bind (fun report -> if report.GameId = gameId then Ok report else Error "event identity")

type MlbScoreboardIngestion(store: MlbScoreboardStore, logger: ILogger<MlbScoreboardIngestion>) =
    static let deadline = TimeSpan.FromSeconds 5.0
    let warned = ConcurrentDictionary<string, ConcurrentDictionary<struct (string * string), unit>>()

    let warnOnce (gameId: string) (frame: string) (fault: string) =
        let digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes frame))
        if warned.GetOrAdd(gameId, fun _ -> ConcurrentDictionary()).TryAdd(struct (digest, fault), ()) then
            logger.LogWarning("Rejected the ESPN MLB scoreboard frame for {GameId}: {Fault}", gameId, fault)

    let guarded (gameId: string) (cancellationToken: CancellationToken) (work: CancellationToken -> Task<bool>) =
        task {
            use limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            limit.CancelAfter deadline
            try
                return! work limit.Token
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException(cancellationToken))
            | :? OperationCanceledException as ex ->
                logger.LogError(ex, "Accepting the ESPN MLB scoreboard frame for {GameId} exceeded its {Deadline} deadline", gameId, deadline)
                return false
            | ex ->
                logger.LogError(ex, "Accepting the ESPN MLB scoreboard frame for {GameId} failed", gameId)
                return false
        }

    static member Deadline = deadline

    member _.AcceptAsync(gameId: string, event: JsonElement, observedAt: DateTime, revision: Nullable<DateTimeOffset>, ct: CancellationToken)
        : Task<bool> =
        task {
            match MlbEspnScoreboard.report gameId event observedAt revision with
            | Error fault ->
                warnOnce gameId (event.GetRawText()) fault
                return false
            | Ok report ->
                return! guarded gameId ct (fun limit ->
                    task {
                        let status = MlbEspnScoreboard.gameStatus null event
                        match! store.AcceptAsync(report, status, limit) with
                        | Ok _ ->
                            if status = "final" || status = "canceled" then warned.TryRemove(gameId) |> ignore
                            return true
                        | Error rejection ->
                            let frame = JsonSerializer.Serialize({ report with ObservedAt = Nullable(); SourceRevisionAt = Nullable() })
                            warnOnce gameId frame (string rejection)
                            return false
                    })
        }

    member _.WithdrawAsync(gameId: string, observedAt: DateTime, revision: Nullable<DateTimeOffset>, ct: CancellationToken) : Task<bool> =
        guarded gameId ct (fun limit ->
            task {
                let! withdrawn = store.WithdrawAsync(gameId, observedAt, revision, limit)
                if withdrawn then warned.TryRemove(gameId) |> ignore
                return withdrawn
            })
