using backend.Services.Espn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry.AspNetCore;

namespace backend.tests;

internal sealed class AppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("EnableScraper", "false");
        builder.UseSetting("EnableProHypeCapture", "false");
        builder.UseSetting("Sentry:Dsn", "");
        builder.UseSetting("OpenTelemetry:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService)
                && (s.ImplementationType?.Assembly == typeof(EspnOddsService).Assembly
                    || s.ImplementationType == typeof(backend.Services.CollegeFootballBackgroundService)
                    || s.ImplementationType == typeof(backend.Services.Highlights.HighlightsBackgroundService))).ToArray())
                services.Remove(worker);
            services.PostConfigure<SentryAspNetCoreOptions>(options => options.DisableUnobservedTaskExceptionCapture());
        });
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableScraper"] = "false",
                ["EnableProHypeCapture"] = "false",
                ["Sentry:Dsn"] = "",
                ["OpenTelemetry:Enabled"] = "false"
            }));
    }
}
