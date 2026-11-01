using System.Net;
using System.Text.Json;
using backend.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace backend.tests;

public sealed class CollegeEspnPaginationTests
{
    private const string EmptyIndex = """{"count":0,"pageIndex":0,"pageSize":25,"pageCount":0,"items":[]}""";
    private static readonly DateTime Date = new(2026, 9, 16);

    private sealed class Provider(string[] pages, string scoreboard) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = QueryHelpers.ParseQuery(uri.Query);
            var body = uri.AbsolutePath switch
            {
                var path when path.EndsWith("/scoreboard") => scoreboard,
                var path when path.EndsWith("/events") => pages[int.Parse(query["page"]!) - 1],
                var path when path.EndsWith("/summary") => JsonSerializer.Serialize(new { header = new { id = query["event"].ToString() } }),
                _ => throw new InvalidOperationException("Unexpected provider request: " + uri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class Fixture(string[] pages, string scoreboard = """{"events":[]}""") : IDisposable
    {
        private readonly HttpClient http = new(new Provider(pages, scoreboard));
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public Task<CollegeSchedule> Schedule() => new CollegeEspnClient(http, TimeProvider.System, cache, NullLogger<CollegeEspnClient>.Instance)
            .ScheduleAsync(Date, 2026, true, CancellationToken.None);
        public void Dispose() { http.Dispose(); cache.Dispose(); }
    }

    [Theory]
    [InlineData(EmptyIndex)]
    [InlineData("""{"count":0,"pageIndex":1,"pageCount":1,"items":[]}""")]
    public async Task Empty_index_does_not_discard_games_present_in_the_scoreboard(string index)
    {
        using var fixture = new Fixture([index], """{"events":[{"id":"101"}]}""");
        var schedule = await fixture.Schedule();
        Assert.Equal("101", Assert.Single(schedule.Events).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Populated_pages_are_all_traversed_and_missing_scoreboard_events_are_recovered()
    {
        using var fixture = new Fixture([
            """{"count":2,"pageIndex":1,"pageCount":2,"items":[{"$ref":"https://sports.core.api.espn.com/v2/events/101?lang=en"}]}""",
            """{"count":2,"pageIndex":2,"pageCount":2,"items":[{"$ref":"https://sports.core.api.espn.com/v2/events/102?lang=en"}]}"""
        ]);
        var schedule = await fixture.Schedule();
        Assert.Equal(new[] { "101", "102" }, schedule.Events.Select(e => e.GetProperty("id").GetString()).Order());
    }

    [Theory]
    [InlineData("""{"count":1,"pageIndex":0,"pageCount":0,"items":[]}""")]
    [InlineData("""{"count":0,"pageIndex":0,"pageCount":0,"items":[{"$ref":"https://sports.core.api.espn.com/v2/events/101?lang=en"}]}""")]
    [InlineData("""{"count":0,"pageIndex":1,"pageCount":0,"items":[]}""")]
    [InlineData("""{"count":0,"pageIndex":1,"items":[]}""")]
    [InlineData("""{"pageIndex":0,"pageCount":0,"items":[]}""")]
    [InlineData("""{"count":0,"pageCount":0,"items":[]}""")]
    [InlineData("""{"count":0,"pageIndex":0,"pageCount":0}""")]
    [InlineData("""{"count":0,"pageIndex":0,"pageCount":0,"items":null}""")]
    [InlineData("""{"count":0,"pageIndex":0,"pageCount":0,"items":{}}""")]
    [InlineData("""{"count":1,"pageIndex":0,"pageCount":1,"items":[]}""")]
    [InlineData("""{"count":0,"pageIndex":1,"pageCount":21,"items":[]}""")]
    public async Task Malformed_metadata_is_not_treated_as_an_empty_day(string index)
    {
        using var fixture = new Fixture([index]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Schedule);
        Assert.Contains("Invalid college event pagination for 2026-09-16", error.Message);
    }

    [Fact]
    public async Task Empty_response_midway_through_populated_pagination_is_rejected()
    {
        using var fixture = new Fixture([
            """{"count":2,"pageIndex":1,"pageCount":2,"items":[{"$ref":"https://sports.core.api.espn.com/v2/events/101?lang=en"}]}""",
            EmptyIndex
        ]);
        await Assert.ThrowsAsync<InvalidDataException>(fixture.Schedule);
    }

    [Fact]
    public async Task Incomplete_nonempty_index_is_still_rejected()
    {
        using var fixture = new Fixture([
            """{"count":2,"pageIndex":1,"pageCount":1,"items":[{"$ref":"https://sports.core.api.espn.com/v2/events/101?lang=en"}]}"""
        ]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Schedule);
        Assert.Equal("Partial college event index", error.Message);
    }
}
