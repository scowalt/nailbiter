namespace Backend.Rendering

open System
open System.Threading
open System.Threading.Channels
open backend.GraphQL.Types

type ISseBroadcaster =
    abstract member PublishAsync: GameUpdate -> CancellationToken -> System.Threading.Tasks.Task
    abstract member Subscribe: topic: string -> ChannelReader<GameUpdate>
    abstract member Unsubscribe: topic: string -> subscription: ChannelReader<GameUpdate> -> unit

type SseBroadcaster() =
    let gate = obj ()
    let mutable subscriptions: Map<string, Channel<GameUpdate> list> = Map.empty

    let publishToTopic (topic: string) (update: GameUpdate) =
        match Volatile.Read(&subscriptions).TryFind topic with
        | Some channels ->
            for ch in channels do
                ch.Writer.TryWrite(update) |> ignore
        | None -> ()

    interface ISseBroadcaster with
        member _.PublishAsync(update: GameUpdate) (_ct: CancellationToken) =
            publishToTopic "live" update
            publishToTopic $"live:{SportExtensions.ToDbValue(update.Sport)}" update
            publishToTopic $"game:{update.GameId}" update
            System.Threading.Tasks.Task.CompletedTask

        member _.Subscribe(topic: string) =
            let ch = Channel.CreateBounded<GameUpdate>(
                BoundedChannelOptions(50, FullMode = BoundedChannelFullMode.DropOldest))
            lock gate (fun () ->
                let channels = subscriptions.TryFind topic |> Option.defaultValue []
                Volatile.Write(&subscriptions, subscriptions.Add(topic, ch :: channels)))
            ch.Reader

        member _.Unsubscribe(topic: string) (subscription: ChannelReader<GameUpdate>) =
            lock gate (fun () ->
                match subscriptions.TryFind topic with
                | Some channels ->
                    let remaining = channels |> List.filter (fun ch -> not (Object.ReferenceEquals(ch.Reader, subscription)))
                    let next = if List.isEmpty remaining then subscriptions.Remove topic else subscriptions.Add(topic, remaining)
                    Volatile.Write(&subscriptions, next)
                | None -> ())
