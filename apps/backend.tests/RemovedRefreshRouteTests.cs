using System.Collections.Concurrent;
using System.Net;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Xunit;

namespace backend.tests;

public sealed class RemovedRefreshRouteTests
{
    private static readonly HttpMethod[] Methods = [HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete];

    [Theory]
    [InlineData("/api/scrape/nfl")]
    [InlineData("/api/f1/refresh/2026")]
    [InlineData("/api/nascar/refresh/2026")]
    [InlineData("/api/indycar/refresh/2026")]
    [InlineData("/api/ratings/refresh")]
    public async Task Removed_refresh_route_is_not_found_for_every_method(string path)
    {
        var providers = new RefusingProviders();
        await using var entry = new AppFactory();
        await using var app = entry.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ExcitementDbContext>>();
            services.RemoveAll<DbContextOptions<ExcitementDbContext>>();
            services.AddDbContext<ExcitementDbContext>(db => db.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1"));
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.PrimaryHandler = providers));
        }));
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var statuses = new List<string>();
        foreach (var method in Methods)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, path));
            statuses.Add($"{method} {(int)response.StatusCode}");
        }

        Assert.Equal(Methods.Select(method => $"{method} 404"), statuses);
        Assert.Empty(providers.Requests);
    }

    private sealed class RefusingProviders : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
