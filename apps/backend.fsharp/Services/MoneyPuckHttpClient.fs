namespace backend.Services

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading

exception internal MoneyPuckInvalidSource of string

type internal MoneyPuckHttpClient(httpClient: HttpClient, timeProvider: TimeProvider, gate: MoneyPuckRefreshGate) =
    do httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Nailbiter-MoneyPuck/1.0 (+https://nailbiter.app)")
    member _.ReadAsync(url: string, maxBytes: int, ct: CancellationToken) = task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0), timeProvider)
        use linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token)
        use request = new HttpRequestMessage(HttpMethod.Get, url)
        use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
        let latchDenial () =
            let header = response.Headers.RetryAfter
            let retryAfter =
                if isNull header then None
                elif header.Date.HasValue then Some header.Date.Value.UtcDateTime
                elif header.Delta.HasValue then
                    let now = timeProvider.GetUtcNow().UtcDateTime
                    Some(if header.Delta.Value > DateTime.MaxValue - now then DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                         else now.Add(header.Delta.Value))
                else None
            gate.LatchDenial(?retryAfter = retryAfter)
        if response.StatusCode = HttpStatusCode.Unauthorized || response.StatusCode = HttpStatusCode.Forbidden
           || response.StatusCode = HttpStatusCode.TooManyRequests then
            latchDenial ()
            raise (HttpRequestException("MoneyPuck access denied; collection paused until refusal backoff expires"))
        response.EnsureSuccessStatusCode() |> ignore
        use! stream = response.Content.ReadAsStreamAsync(linked.Token)
        use body = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let! count = stream.ReadAsync(buffer.AsMemory(0, min buffer.Length (maxBytes + 1 - int body.Length)), linked.Token)
            if count = 0 then finished <- true
            else
                body.Write(buffer, 0, count)
                if body.Length > int64 maxBytes then finished <- true
        let bytes = body.ToArray()
        if Regex.IsMatch(Encoding.UTF8.GetString(bytes), @"cf-chl-|captcha|verify (?:that )?you are human|checking your browser|just a moment|access denied|_cf_chl_opt|challenge-platform/h/|enable javascript and cookies", RegexOptions.IgnoreCase) then
            latchDenial ()
            raise (HttpRequestException("MoneyPuck challenge; collection paused until refusal backoff expires"))
        if body.Length > int64 maxBytes || (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > int64 maxBytes) then
            raise (MoneyPuckInvalidSource "response_too_large")
        return UTF8Encoding(false, true).GetString(bytes)
    }
