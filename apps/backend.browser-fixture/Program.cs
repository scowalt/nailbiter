using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotNet.Testcontainers.Configurations;
using Testcontainers.PostgreSql;

namespace BrowserFixture;

internal static class FixtureProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await RunAsync(args);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Fixture setup failed: {error.GetType().Name}");
            return 1;
        }
    }

    private static ushort SelectDisposableHostPort()
    {
        using var reservation = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            ExclusiveAddressUse = true
        };
        reservation.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return checked((ushort)((IPEndPoint)reservation.LocalEndPoint!).Port);
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] is not ("--highlights" or "--grouped-home" or "--series")))
            throw new ArgumentException("Usage: backend.browser-fixture <manifest-path> [--highlights|--grouped-home|--series] (run through Portless --tailscale)");
        var highlights = args.Length == 2 && args[1] == "--highlights";
        var groupedHome = args.Length == 2 && args[1] == "--grouped-home";
        var series = args.Length == 2 && args[1] == "--series";
        var localUrl = RequiredUrl("PORTLESS_URL");
        var privateUrl = RequiredUrl("PORTLESS_TAILSCALE_URL");
        if (Environment.GetEnvironmentVariable("PORTLESS_FUNNEL") == "1"
            || Environment.GetEnvironmentVariable("PORTLESS_NGROK") == "1"
            || Environment.GetEnvironmentVariable("HOST") != "127.0.0.1"
            || !int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Private Portless loopback hosting is required; refusing to start.");

        var retained = new HashSet<string>(StringComparer.Ordinal)
            { "PATH", "HOME", "USER", "TMPDIR", "DOTNET_ROOT" };
        foreach (string key in Environment.GetEnvironmentVariables().Keys)
            if (!retained.Contains(key)) Environment.SetEnvironmentVariable(key, null);

        using var shutdown = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            { context.Cancel = true; shutdown.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        var dockerEndpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        if (dockerEndpoint.Scheme != "unix")
            throw new InvalidOperationException("This fixture requires a local Unix Docker endpoint.");
        TestcontainersSettings.DockerSocketOverride = dockerEndpoint.AbsolutePath;
        TestcontainersSettings.ResourceReaperEnabled = true;
#pragma warning disable CS0618
        TestcontainersSettings.ResourceReaperPublicHostPort = _ =>
        {
            var selectedPort = SelectDisposableHostPort();
            Console.WriteLine($"Disposable Ryuk host port: {selectedPort}");
            return selectedPort;
        };
#pragma warning restore CS0618
        await using var database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nailbiter_browser_fixture").WithUsername("fixture").WithPassword(Guid.NewGuid().ToString("N"))
            .WithPortBinding("61000-65535", PostgreSqlBuilder.PostgreSqlPort.ToString(CultureInfo.InvariantCulture))
            .Build();
        var root = Directory.CreateTempSubdirectory("nailbiter-browser-fixture-").FullName;
        try
        {
            await database.StartAsync(shutdown.Token);
            Console.WriteLine($"Disposable PostgreSQL host port: {database.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort)}");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", root);
            Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_BACKEND", root);
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", database.GetConnectionString());
            Environment.SetEnvironmentVariable("EnableScraper", "false");
            Environment.SetEnvironmentVariable("Sentry__Dsn", "");
            Environment.SetEnvironmentVariable("OpenTelemetry__Enabled", "false");
            var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../backend/wwwroot"));
            await using var host = new FixtureHost(database.GetConnectionString(), root, webRoot, port, highlights, groupedHome, series);
            host.StartServer();
            var manifest = new
            {
                version = 1,
                localUrl,
                privateUrl,
                homePath = "/",
                listingPath = "/nba",
                listingDateEastern = host.ListingDate.ToString("yyyy-MM-dd"),
                intended = FixtureHost.Intended,
                distraction = FixtureHost.Distraction,
                databaseContainerId = database.Id,
                highlights = highlights ? new { completed = HighlightsFixture.Completed, hidden = HighlightsFixture.Hidden.Select(s => $"mlb-{s}"), controlPath = args[0] + ".control" } : null,
                groupedHome = groupedHome ? new { now = GroupedHomeFixture.Now, controlPath = args[0] + ".control" } : null,
                series = series ? new { cards = SeriesFixture.Cards, live = SeriesFixture.Live, controlPath = args[0] + ".control" } : null
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            await File.WriteAllTextAsync(args[0] + ".pending", json, shutdown.Token);
            File.Move(args[0] + ".pending", args[0]);
            Console.WriteLine("Browser fixture ready (deterministic setup only; no AI evaluation).");
            if (highlights || groupedHome || series) await FixtureControl.RunAsync(args[0] + ".control", host, shutdown.Token);
            else
            {
                try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static string RequiredUrl(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException($"{name} is missing: launch through Portless --tailscale, no fallback.");
            return value.TrimEnd('/');
        }
    }
}
