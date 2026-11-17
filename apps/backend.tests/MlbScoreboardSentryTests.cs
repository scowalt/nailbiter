using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using backend.Data;
using backend.Services;
using backend.Services.Espn;
using backend.Services.Kalshi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace backend.tests;

[Collection(nameof(SentryGlobalHubCollection))]
public sealed class MlbScoreboardSentryTests : IClassFixture<SharedPostgreSql>, IAsyncLifetime
{
    private static readonly JsonObject Espn = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "MlbScoreboard", "espn-basic.json")))!.AsObject();

    private readonly SyntheticMlbProvider provider = new(Espn);
    private readonly RawTransport transport = new();
    private readonly HostedGameFixture host;

    public MlbScoreboardSentryTests(SharedPostgreSql server)
    {
        host = new(Served("opening"), services =>
        {
            services.PostConfigure<SentryAspNetCoreOptions>(o => { o.Dsn = "https://public@sentry.invalid/1"; o.Transport = transport; });
            services.AddTransient(s => new KalshiOddsService(new HttpClient(provider), s.GetRequiredService<ILogger<KalshiOddsService>>(),
                s.GetRequiredService<TimeProvider>()));
            services.AddTransient(s => new EspnOddsService(new HttpClient(provider), s.GetRequiredService<ILogger<EspnOddsService>>(),
                s, s.GetRequiredService<IGameUpdateService>(), s.GetRequiredService<KalshiOddsService>()));
        }, server);
    }

    public Task InitializeAsync() => host.InitializeAsync();
    public Task DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task A_corrupt_stored_frame_sends_one_error_event_without_its_payload_and_the_next_frame_replaces_it()
    {
        await Observe("opening");
        await using (var connection = new NpgsqlConnection(ConnectionString()))
        {
            await connection.OpenAsync();
            await using var corrupt = new NpgsqlCommand("UPDATE games SET mlb_scoreboard_json = '{\"gameId\":\"SECRETPAYLOAD' WHERE id = '991300104'", connection);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        for (var i = 0; i < 3; i++)
            Assert.Equal(MlbScoreboardPhase.Unavailable, (await Read()).Phase);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(Read)));
        await Observe("advance");

        var sent = Assert.Single(await Events());
        Assert.Equal("error", sent.Level);
        Assert.Equal("backend.Services.MlbScoreboardStore", sent.Logger);
        Assert.DoesNotContain("SECRETPAYLOAD", sent.Json);
        Assert.Equal("Bottom 6th", (await Read()).Board.Value.Detail);
    }

    [Fact]
    public async Task Malformed_missing_and_unusable_boards_send_no_event_however_many_cycles_see_them()
    {
        await Observe("opening");
        provider.Corrupt("991300104", e => e["competitions"]![0]!["competitors"]![1]!["linescores"]![0]!["value"] = "x");
        provider.Corrupt("991300108", e => e["competitions"]![0]!["competitors"]![0]!.AsObject().Remove("score"));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(30));
            await Run();
        }
        foreach (var response in new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.NotFound),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"events\":[", System.Text.Encoding.UTF8, "application/json") },
        })
        {
            provider.Scoreboards = response;
            host.Clock.Advance(TimeSpan.FromSeconds(30));
            await Run();
        }

        Assert.Empty(await Events());
        Assert.Equal("Top 5th", (await Read()).Board.Value.Detail);
    }

    [Fact]
    public async Task An_acceptance_deadline_sends_one_error_event_per_cycle_it_stops()
    {
        await Observe("opening");
        await using var holder = new NpgsqlConnection(ConnectionString());
        await holder.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(683, hashtext('991300104'))", holder))
            await hold.ExecuteNonQueryAsync();

        await Observe("advance");

        var sent = Assert.Single(await Events());
        Assert.Equal("error", sent.Level);
        Assert.Equal("backend.Services.MlbScoreboardIngestion", sent.Logger);
        Assert.Contains("991300104", sent.Json);
        Assert.Equal("Top 5th", (await Read()).Board.Value.Detail);
    }

    [Fact]
    public async Task A_failing_acceptance_sends_one_error_event_and_keeps_the_last_frame()
    {
        await Observe("opening");
        await using (var connection = new NpgsqlConnection(ConnectionString()))
        {
            await connection.OpenAsync();
            await using var fail = new NpgsqlCommand("""
                CREATE FUNCTION fail_scoreboard_write() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'synthetic scoreboard write failure'; END $$;
                CREATE TRIGGER fail_scoreboard_write BEFORE UPDATE ON games FOR EACH ROW
                WHEN (NEW.id = '991300104' AND NEW.mlb_scoreboard_json IS DISTINCT FROM OLD.mlb_scoreboard_json)
                EXECUTE FUNCTION fail_scoreboard_write();
                """, connection);
            await fail.ExecuteNonQueryAsync();
        }

        await Observe("advance");

        var sent = Assert.Single(await Events());
        Assert.Equal("error", sent.Level);
        Assert.Equal("backend.Services.MlbScoreboardIngestion", sent.Logger);
        Assert.Contains("991300104", sent.Json);
        Assert.Contains("Accepting the ESPN MLB scoreboard frame for {GameId} failed", sent.Json);
        Assert.Equal("Top 5th", (await Read()).Board.Value.Detail);
    }

    [Fact]
    public async Task A_failing_withdrawal_sends_one_error_event_named_for_the_withdrawal_and_keeps_the_game()
    {
        await Observe("opening");
        await using (var connection = new NpgsqlConnection(ConnectionString()))
        {
            await connection.OpenAsync();
            await using var fail = new NpgsqlCommand("""
                CREATE FUNCTION fail_withdrawal() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'synthetic withdrawal failure'; END $$;
                CREATE TRIGGER fail_withdrawal BEFORE UPDATE ON games FOR EACH ROW
                WHEN (NEW.id = '991300106' AND NEW.status = 'canceled')
                EXECUTE FUNCTION fail_withdrawal();
                """, connection);
            await fail.ExecuteNonQueryAsync();
        }
        provider.Withdraw("991300106");

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Run();

        var sent = Assert.Single(await Events());
        Assert.Equal(("error", "backend.Services.MlbScoreboardIngestion"), (sent.Level, sent.Logger));
        Assert.Contains("991300106", sent.Json);
        Assert.Contains("Withdrawing ESPN MLB game {GameId}, which left its scoreboard, failed", sent.Json);
        Assert.DoesNotContain("Accepting", sent.Json);
        Assert.Equal(MlbScoreboardPhase.Pregame, (await Read("991300106")).Phase);
    }

    [Fact]
    public async Task A_withdrawal_deadline_sends_one_error_event_named_for_the_withdrawal()
    {
        await Observe("opening");
        await using var holder = new NpgsqlConnection(ConnectionString());
        await holder.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(683, hashtext('991300106'))", holder))
            await hold.ExecuteNonQueryAsync();
        provider.Withdraw("991300106");

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Run();

        var sent = Assert.Single(await Events());
        Assert.Equal(("error", "backend.Services.MlbScoreboardIngestion"), (sent.Level, sent.Logger));
        Assert.Contains("991300106", sent.Json);
        Assert.Contains("Withdrawing ESPN MLB game {GameId}, which left its scoreboard, exceeded its {Deadline} deadline", sent.Json);
        Assert.DoesNotContain("Accepting", sent.Json);
    }

    private async Task<MlbScoreboardView> Read(string gameId) =>
        (await host.Services.GetRequiredService<MlbScoreboardStore>().ReadAsync(gameId, CancellationToken.None)).Value;

    private async Task<MlbScoreboardView> Read() =>
        (await host.Services.GetRequiredService<MlbScoreboardStore>().ReadAsync("991300104", CancellationToken.None)).Value;

    private string ConnectionString()
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExcitementDbContext>().Database.GetConnectionString()!;
    }

    private static DateTimeOffset Served(string observation) =>
        DateTimeOffset.Parse((string)Espn["observations"]!.AsArray().Single(o => (string)o!["id"]! == observation)!["servedAt"]!);

    private async Task Observe(string observation)
    {
        provider.Serve(observation);
        host.Clock.SetUtcNow(Served(observation));
        await Run();
    }

    private async Task Run()
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EspnOddsService>().ScrapeAndStoreOddsAsync("mlb", null);
    }

    private async Task<Sent[]> Events()
    {
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
        return [.. transport.Events.Select(json =>
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            return new Sent(root.TryGetProperty("level", out var level) ? level.GetString() : null,
                root.TryGetProperty("logger", out var logger) ? logger.GetString() : null, json);
        })];
    }

    private sealed record Sent(string? Level, string? Logger, string Json);

    private sealed class RawTransport : ITransport
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            for (var i = 1; i + 1 < lines.Length; i++)
            {
                using var header = JsonDocument.Parse(lines[i]);
                if (header.RootElement.GetProperty("type").GetString() != "event") continue;
                Events.Enqueue(lines[++i]);
            }
        }
    }
}
