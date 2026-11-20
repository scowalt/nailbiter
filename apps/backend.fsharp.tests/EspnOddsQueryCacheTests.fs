module EspnOddsQueryCacheTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Diagnostics
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open backend.Data
open backend.Services
open backend.Services.Espn

type private QueryCompilations() =
    let compiled = Collections.Concurrent.ConcurrentQueue<string>()
    member _.Since(mark: int) = compiled |> Seq.skip mark |> Seq.toList
    member _.Count = compiled.Count
    interface IQueryExpressionInterceptor with
        member _.QueryCompilationStarting(expression, _) =
            compiled.Enqueue(expression.ToString())
            expression

type private MovableClock(start: DateTime) =
    inherit TimeProvider()
    member val Now = start with get, set
    override this.GetUtcNow() = DateTimeOffset(this.Now)

[<Fact>]
let ``a later live odds cycle reuses the compiled queries of the previous one`` () = task {
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    let compilations = QueryCompilations()
    let clock = MovableClock(DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc))
    let services = ServiceCollection()
    services.AddSingleton<TimeProvider>(clock) |> ignore
    services.AddDbContext<ExcitementDbContext>(fun options ->
        options.UseSqlite(connection).AddInterceptors(compilations) |> ignore) |> ignore
    use provider = services.BuildServiceProvider()
    do!
        task {
            use scope = provider.CreateScope()
            let! _ = scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.EnsureCreatedAsync()
            ()
        }
    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(_, _) =
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent("{\"events\":[]}")
                Task.FromResult(response) }
    use http = new HttpClient(handler)
    let updates =
        { new IGameUpdateService with
            member _.PublishUpdateAsync(_, _) = Task.CompletedTask }
    let cycle () = task {
        use scope = provider.CreateScope()
        let scraper = EspnOddsService(http, NullLogger<EspnOddsService>.Instance, scope.ServiceProvider, updates, Unchecked.defaultof<_>)
        let! _ = scraper.ScrapeAndStoreOddsAsync("nba", CancellationToken.None)
        ()
    }

    do! cycle ()
    let warm = compilations.Count
    clock.Now <- clock.Now.AddDays(1.0).AddSeconds(30.0)
    do! cycle ()

    Assert.Empty(compilations.Since warm)
}
