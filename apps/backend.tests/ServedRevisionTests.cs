using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace backend.tests;

public sealed class ServedRevisionTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("/health")]
    [InlineData("/no-such-page")]
    public async Task Every_response_names_the_source_revision_the_image_was_built_from(string path)
    {
        await using var app = new AppFactory().WithWebHostBuilder(builder => builder.UseSetting("SourceRevision", Revision));
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(new[] { Revision }, response.Headers.GetValues("X-Nailbiter-Revision"));
    }

    [Fact]
    public async Task A_build_without_a_source_revision_sends_no_revision_header()
    {
        await using var app = new AppFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(response.Headers.Contains("X-Nailbiter-Revision"));
    }
}
