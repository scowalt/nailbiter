using System.Net;
using Xunit;

namespace backend.tests;

public sealed class LeagueDateQueryHost : IAsyncLifetime
{
    internal HostedGameFixture Host { get; } = new();

    public Task InitializeAsync() => Host.InitializeAsync();

    public Task DisposeAsync() => Host.DisposeAsync();
}

public sealed class LeagueDateQueryTests(LeagueDateQueryHost database) : IClassFixture<LeagueDateQueryHost>
{
    [Theory]
    [InlineData("2026-03-08T12:00:00Z")]
    [InlineData("2026-03-08T12:00:00+09:00")]
    public async Task Date_with_a_time_or_offset_does_not_fail_the_page(string date)
    {
        using var response = await database.Host.Client.GetAsync($"/nba?date={Uri.EscapeDataString(date)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
