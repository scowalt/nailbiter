namespace Backend.Rendering

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Channels
open backend.GraphQL.Types

type ISseBroadcaster =
    abstract member PublishAsync: GameUpdate -> CancellationToken -> System.Threading.Tasks.Task
    abstract member Subscribe: topic: string -> ChannelReader<GameUpdate>
    abstract member Unsubscribe: topic: string -> subscription: ChannelReader<GameUpdate> -> unit

type SseBroadcaster() =
    let subscriptions = ConcurrentDictionary<string, ConcurrentBag<Channel<GameUpdate>>>()

    let getOrCreateTopic (topic: string) =
        subscriptions.GetOrAdd(topic, fun _ -> ConcurrentBag<Channel<GameUpdate>>())

    let publishToTopic (topic: string) (update: GameUpdate) =
        match subscriptions.TryGetValue(topic) with
        | true, channels ->
            for ch in channels do
                ch.Writer.TryWrite(update) |> ignore
        | _ -> ()

    interface ISseBroadcaster with
        member _.PublishAsync(update: GameUpdate) (_ct: CancellationToken) =
            publishToTopic "live" update
            publishToTopic $"live:{SportExtensions.ToDbValue(update.Sport)}" update
            publishToTopic $"game:{update.GameId}" update
            System.Threading.Tasks.Task.CompletedTask

        member _.Subscribe(topic: string) =
            let ch = Channel.CreateBounded<GameUpdate>(
                BoundedChannelOptions(50, FullMode = BoundedChannelFullMode.DropOldest))
            let bag = getOrCreateTopic topic
            bag.Add(ch)
            ch.Reader

        member _.Unsubscribe(topic: string) (subscription: ChannelReader<GameUpdate>) =
            match subscriptions.TryGetValue(topic) with
            | true, channels ->
                let remaining = ConcurrentBag<Channel<GameUpdate>>()
                for ch in channels do
                    if not (Object.ReferenceEquals(ch.Reader, subscription)) then
                        remaining.Add(ch)
                subscriptions.TryUpdate(topic, remaining, channels) |> ignore
            | _ -> ()
