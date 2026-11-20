module SseBroadcasterTests

open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Backend.Rendering
open backend.GraphQL.Types
open backend.Models

let private update () =
    GameUpdate(GameId = "1", Sport = Sport.NBA, HomeTeam = "LAL", AwayTeam = "BOS", Status = GameStatus.IN_PROGRESS)

let private received (reader: ChannelReader<GameUpdate>) =
    let mutable count = 0
    let mutable item = Unchecked.defaultof<GameUpdate>
    while reader.TryRead(&item) do
        count <- count + 1
    count

[<Fact>]
let ``concurrent subscribers keep receiving and unsubscribed readers stop`` () =
    for _ in 1..50 do
        let broadcaster = SseBroadcaster() :> ISseBroadcaster
        let workers = 16
        let start = new Barrier(workers)
        let results =
            [| for _ in 1..workers ->
                Task.Run(fun () ->
                    start.SignalAndWait()
                    let kept = broadcaster.Subscribe "live"
                    let dropped = broadcaster.Subscribe "live"
                    broadcaster.Unsubscribe "live" dropped
                    kept, dropped) |]
            |> Task.WhenAll
            |> _.Result

        (broadcaster.PublishAsync (update ()) CancellationToken.None).Wait()

        for kept, dropped in results do
            Assert.Equal(1, received kept)
            Assert.Equal(0, received dropped)
