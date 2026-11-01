using System.Net;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Options;

namespace backend.Services.Highlights;

public sealed class YouTubeHighlightsClient(IHttpClientFactory clients, IOptions<YouTubeHighlightsOptions> options, TimeProvider clock)
{
    public const string ApprovedChannelId = "UCoLrcjPV5PbUrUyXq5mjc_A";
    public const string HttpClientName = "YouTubeHighlights";

    private const int MaxRequestAuthorizationAttempts = 3;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly TimeZoneInfo QuotaZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static readonly string[] IsoCountryRegions = "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW".Split(' ');
    private DateOnly quotaDay;
    private int dailyUnits;
    private DateTimeOffset? retryAtUtc;

    private DateTimeOffset NextQuotaDay()
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), QuotaZone);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified), QuotaZone));
    }

    private sealed class Failure(YouTubeOutcome outcome, DateTimeOffset? retryAtUtc = null) : Exception
    {
        public YouTubeOutcome Outcome { get; } = outcome;
        public DateTimeOffset? RetryAtUtc { get; } = retryAtUtc;
    }

    private sealed class Session(HttpClient http, CancellationToken cancellationToken, Func<CancellationToken, Task<YouTubeRequestAuthorization>>? authorizeRequest)
    {
        public HttpClient Http { get; } = http;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Func<CancellationToken, Task<YouTubeRequestAuthorization>>? AuthorizeRequest { get; } = authorizeRequest;
        public int Units { get; set; }
        public string[]? Regions { get; set; }
    }

    private static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static string? Text(JsonElement value, string name) => Get(value, name).ValueKind == JsonValueKind.String ? Get(value, name).GetString() : null;
    private static JsonElement[] Items(JsonElement value) => Get(value, "items").ValueKind == JsonValueKind.Array ? Get(value, "items").EnumerateArray().ToArray() : throw new Failure(YouTubeOutcome.InvalidResponse);

    private async Task<JsonDocument> RequestAsync(Session session, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/" + path);
        request.Headers.Add("x-goog-api-key", options.Value.ApiKey);
        session.CancellationToken.ThrowIfCancellationRequested();
        RefreshQuotaDay();
        if (dailyUnits >= options.Value.DailyUnitLimit) throw new Failure(YouTubeOutcome.BudgetExhausted, NextQuotaDay());
        if (session.AuthorizeRequest is not null)
        {
            var authorized = false;
            for (var attempt = 0; attempt < MaxRequestAuthorizationAttempts; attempt++)
            {
                var authorization = await session.AuthorizeRequest(session.CancellationToken);
                session.CancellationToken.ThrowIfCancellationRequested();
                if (!authorization.Permitted) throw new Failure(YouTubeOutcome.BudgetExhausted, NextQuotaDay());
                var currentDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), QuotaZone).Date);
                if (authorization.PacificDay > currentDay) throw new Failure(YouTubeOutcome.Backoff);
                if (authorization.PacificDay == currentDay) { authorized = true; break; }
            }
            if (!authorized) throw new Failure(YouTubeOutcome.Backoff);
            RefreshQuotaDay();
            if (dailyUnits >= options.Value.DailyUnitLimit) throw new Failure(YouTubeOutcome.BudgetExhausted, NextQuotaDay());
        }
        dailyUnits++;
        session.Units++;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await session.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var body = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
        {
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (body.Length + read > 2 * 1024 * 1024) throw new Failure(YouTubeOutcome.InvalidResponse);
                body.Write(buffer, 0, read);
            }
        }
        var bytes = body.ToArray();
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            var until = retry?.Date ?? clock.GetUtcNow().Add(retry?.Delta ?? TimeSpan.FromMinutes(15));
            if (until < clock.GetUtcNow().AddMinutes(15)) until = clock.GetUtcNow().AddMinutes(15);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new Failure(YouTubeOutcome.Throttled, until);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                try
                {
                    using var error = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                    var errors = Get(Get(error.RootElement, "error"), "errors");
                    if (errors.ValueKind == JsonValueKind.Array)
                    {
                        if (errors.EnumerateArray().Any(item => Text(item, "reason") is "quotaExceeded" or "dailyLimitExceeded"))
                            throw new Failure(YouTubeOutcome.QuotaExhausted, NextQuotaDay());
                        if (errors.EnumerateArray().Any(item => Text(item, "reason") is "rateLimitExceeded" or "userRateLimitExceeded"))
                            throw new Failure(YouTubeOutcome.Throttled, until);
                    }
                }
                catch (JsonException) { }
                throw new Failure(YouTubeOutcome.InvalidConfiguration);
            }
            throw new Failure(response.StatusCode == HttpStatusCode.Unauthorized ? YouTubeOutcome.InvalidConfiguration : YouTubeOutcome.TemporaryFailure, until);
        }
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (Get(document.RootElement, "error").ValueKind != JsonValueKind.Undefined)
        {
            document.Dispose();
            throw new Failure(YouTubeOutcome.InvalidResponse);
        }
        return document;
    }

    private async Task<YouTubeHighlightsResult> ExecuteAsync(Func<Session, Task<YouTubeHighlightsResult>> operation, CancellationToken cancellationToken,
        Func<CancellationToken, Task<YouTubeRequestAuthorization>>? authorizeRequest = null)
    {
        if (options.Value.ValidateConfiguration() is YouTubeOutcome problem) return new(problem, [], null, 0, null);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            RefreshQuotaDay();
            if (dailyUnits >= options.Value.DailyUnitLimit) return new(YouTubeOutcome.BudgetExhausted, [], null, 0, NextQuotaDay());
            if (retryAtUtc > clock.GetUtcNow()) return new(YouTubeOutcome.Backoff, [], null, 0, retryAtUtc);
            using var http = clients.CreateClient(HttpClientName);
            var session = new Session(http, cancellationToken, authorizeRequest);
            try { return await operation(session); }
            catch (Failure failure) { return Failed(failure.Outcome, session.Units, failure.RetryAtUtc); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            catch (Exception failure) when (failure is HttpRequestException or IOException or OperationCanceledException)
            { return Failed(YouTubeOutcome.TemporaryFailure, session.Units); }
            catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or ArgumentException or XmlException or OverflowException)
            { return Failed(YouTubeOutcome.InvalidResponse, session.Units); }
        }
        finally { operationGate.Release(); }
    }

    private void RefreshQuotaDay()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), QuotaZone).Date);
        if (today != quotaDay) { quotaDay = today; dailyUnits = 0; }
    }

    private YouTubeHighlightsResult Failed(YouTubeOutcome outcome, int units, DateTimeOffset? retry = null)
    {
        retryAtUtc = retry ?? clock.GetUtcNow().AddMinutes(15);
        return new(outcome, [], null, units, retryAtUtc);
    }

    private async Task<string> VerifyChannelAsync(Session session)
    {
        using var channel = await RequestAsync(session, "channels?part=contentDetails&id=" + ApprovedChannelId);
        var channels = Items(channel.RootElement);
        if (channels.Length != 1 || Text(channels[0], "id") != ApprovedChannelId) throw new Failure(YouTubeOutcome.ChannelUnverified);
        var playlist = Text(Get(Get(channels[0], "contentDetails"), "relatedPlaylists"), "uploads");
        if (string.IsNullOrWhiteSpace(playlist) || playlist.Length > 100) throw new Failure(YouTubeOutcome.ChannelUnverified);
        return playlist;
    }

    private static bool ValidVideoId(string? id) => id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidToken(string? token) => token is null || token.Length is > 0 and <= 512 && token.All(c => !char.IsControl(c));

    public Task<YouTubeHighlightsResult> DiscoverUploadsAsync(string? pageToken = null, CancellationToken cancellationToken = default)
    {
        if (!ValidToken(pageToken)) return Task.FromResult(new YouTubeHighlightsResult(YouTubeOutcome.InvalidRequest, [], null, 0, null));
        return ExecuteAsync(async session =>
        {
            var playlist = await VerifyChannelAsync(session);
            var ids = new List<string>();
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var next = pageToken;
            for (var page = 0; page < options.Value.MaxUploadPages; page++)
            {
                if (next is not null && !tokens.Add(next)) throw new Failure(YouTubeOutcome.InvalidResponse);
                var path = "playlistItems?part=contentDetails&maxResults=50&playlistId=" + Uri.EscapeDataString(playlist);
                if (next is not null) path += "&pageToken=" + Uri.EscapeDataString(next);
                using var uploads = await RequestAsync(session, path);
                var items = Items(uploads.RootElement);
                if (items.Length > 50) throw new Failure(YouTubeOutcome.InvalidResponse);
                foreach (var item in items)
                {
                    var id = Text(Get(item, "contentDetails"), "videoId");
                    if (!ValidVideoId(id)) throw new Failure(YouTubeOutcome.InvalidResponse);
                    if (!ids.Contains(id!)) ids.Add(id!);
                }
                if (Get(uploads.RootElement, "nextPageToken").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)) throw new Failure(YouTubeOutcome.InvalidResponse);
                next = Text(uploads.RootElement, "nextPageToken");
                if (!ValidToken(next) || next is not null && tokens.Contains(next)) throw new Failure(YouTubeOutcome.InvalidResponse);
                if (next is null) break;
            }
            var evidence = new List<YouTubeVideoEvidence>();
            foreach (var batch in ids.Chunk(50)) evidence.AddRange(await ReadVideosAsync(session, batch));
            return new(YouTubeOutcome.Success, evidence, next, session.Units, null);
        }, cancellationToken);
    }

    public Task<YouTubeHighlightsResult> ValidateVideosAsync(IReadOnlyCollection<string> videoIds, CancellationToken cancellationToken = default) =>
        ValidateVideosAsync(videoIds, cancellationToken, null);

    internal Task<YouTubeHighlightsResult> ValidateVideosAsync(IReadOnlyCollection<string> videoIds, CancellationToken cancellationToken,
        Func<CancellationToken, Task<YouTubeRequestAuthorization>>? authorizeRequest)
    {
        if (videoIds is null || videoIds.Count > 200 || videoIds.Any(id => !ValidVideoId(id)))
            return Task.FromResult(new YouTubeHighlightsResult(YouTubeOutcome.InvalidRequest, [], null, 0, null));
        var ids = videoIds.Distinct().ToArray();
        return ExecuteAsync(async session =>
        {
            if (ids.Length == 0) return new(YouTubeOutcome.Success, [], null, 0, null);
            await VerifyChannelAsync(session);
            var evidence = new List<YouTubeVideoEvidence>();
            foreach (var batch in ids.Chunk(50)) evidence.AddRange(await ReadVideosAsync(session, batch));
            return new(YouTubeOutcome.Success, evidence, null, session.Units, null);
        }, cancellationToken, authorizeRequest);
    }

    private async Task<YouTubeVideoEvidence[]> ReadVideosAsync(Session session, string[] ids)
    {
        using var videos = await RequestAsync(session, "videos?part=snippet,contentDetails,status&id=" + string.Join(',', ids));
        var items = Items(videos.RootElement);
        if (Get(videos.RootElement, "nextPageToken").ValueKind != JsonValueKind.Undefined) throw new Failure(YouTubeOutcome.InvalidResponse);
        if (items.Any(item => !ids.Contains(Text(item, "id"))) || items.Select(item => Text(item, "id")).Distinct().Count() != items.Length)
            throw new Failure(YouTubeOutcome.InvalidResponse);
        var evidence = new List<YouTubeVideoEvidence>();
        foreach (var id in ids)
        {
            var item = items.SingleOrDefault(item => Text(item, "id") == id);
            if (item.ValueKind == JsonValueKind.Undefined)
            {
                evidence.Add(new(id, YouTubeAvailability.Removed, null, null, null, null, null, null, [], [], clock.GetUtcNow()));
                continue;
            }
            var snippet = Get(item, "snippet");
            var status = Get(item, "status");
            var title = Text(snippet, "title");
            var description = Text(snippet, "description");
            var channel = Text(snippet, "channelId");
            var durationText = Text(Get(item, "contentDetails"), "duration");
            var broadcast = Text(snippet, "liveBroadcastContent");
            var uploadStatus = Text(status, "uploadStatus");
            if (string.IsNullOrWhiteSpace(channel) || channel.Length > 100 || string.IsNullOrWhiteSpace(title) || title.Length > 500 ||
                description is null || description.Length > 10000 || durationText is null || durationText.Length > 64 ||
                !DateTimeOffset.TryParse(Text(snippet, "publishedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var publishedAt) ||
                broadcast is not ("none" or "live" or "upcoming") || Text(status, "privacyStatus") is not ("public" or "unlisted" or "private") ||
                uploadStatus is not ("processed" or "uploaded" or "failed" or "rejected" or "deleted") ||
                Get(status, "embeddable").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new Failure(YouTubeOutcome.InvalidResponse);
            var notProcessed = broadcast != "none" || uploadStatus != "processed";
            var duration = XmlConvert.ToTimeSpan(durationText);
            if (duration < TimeSpan.Zero || duration == TimeSpan.Zero && !notProcessed) throw new Failure(YouTubeOutcome.InvalidResponse);
            var kid = Get(status, "madeForKids");
            if (kid.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False)) throw new Failure(YouTubeOutcome.InvalidResponse);
            bool? madeForKids = kid.ValueKind is JsonValueKind.True or JsonValueKind.False ? kid.GetBoolean() : null;
            var availability = Text(status, "privacyStatus") == "private" ? YouTubeAvailability.Private
                : Text(snippet, "channelId") != ApprovedChannelId ? YouTubeAvailability.UnapprovedPublisher
                : Get(status, "embeddable").ValueKind == JsonValueKind.False ? YouTubeAvailability.NotEmbeddable
                : notProcessed ? YouTubeAvailability.NotProcessed
                : madeForKids == true ? YouTubeAvailability.MadeForKids
                : !madeForKids.HasValue ? YouTubeAvailability.UnknownMadeForKids : YouTubeAvailability.Playable;
            var restriction = Get(Get(item, "contentDetails"), "regionRestriction");
            var allowed = RegionList(restriction, "allowed");
            var blocked = RegionList(restriction, "blocked");
            if (allowed is not null && blocked is not null || restriction.ValueKind != JsonValueKind.Undefined && allowed is null && blocked is null)
                throw new Failure(YouTubeOutcome.InvalidResponse);
            if (allowed is { Length: 0 }) availability = YouTubeAvailability.GloballyBlocked;
            else if (allowed is { Length: > 0 } || blocked is { Length: > 0 })
            {
                if (session.Regions is null)
                {
                    using var regions = await RequestAsync(session, "i18nRegions?part=snippet");
                    var countries = Items(regions.RootElement).Select(region => Text(region, "id")).ToArray();
                    if (countries.Length is < 1 or > 500 || countries.Any(country => !ValidRegion(country)) || countries.Distinct().Count() != countries.Length)
                        throw new Failure(YouTubeOutcome.InvalidResponse);
                    session.Regions = countries.Select(country => country!).ToArray();
                }
                var countryRegions = IsoCountryRegions.Union(session.Regions).ToArray();
                if ((allowed ?? []).Concat(blocked ?? []).Any(country => !countryRegions.Contains(country))) throw new Failure(YouTubeOutcome.InvalidResponse);
                if (blocked is not null && countryRegions.All(country => blocked.Contains(country)))
                    availability = YouTubeAvailability.GloballyBlocked;
            }
            evidence.Add(new(id, availability, channel, title, description,
                publishedAt, duration,
                madeForKids, allowed ?? [], blocked ?? [], clock.GetUtcNow()));
        }
        return evidence.ToArray();
    }

    private static bool ValidRegion(string? value) => value is { Length: 2 } && value.All(c => c is >= 'A' and <= 'Z');

    private static string[]? RegionList(JsonElement restriction, string name)
    {
        var value = Get(restriction, name);
        if (value.ValueKind == JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 500) throw new Failure(YouTubeOutcome.InvalidResponse);
        var countries = value.EnumerateArray().Select(country => country.ValueKind == JsonValueKind.String ? country.GetString() : null).ToArray();
        if (countries.Any(country => !ValidRegion(country))) throw new Failure(YouTubeOutcome.InvalidResponse);
        return countries.Select(country => country!).Distinct().ToArray();
    }
}
