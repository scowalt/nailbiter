module FormatHelpersTests

open System
open Xunit
open Backend.Rendering
open backend.GraphQL.Types

[<Fact>]
let ``formatProbability - null returns dash`` () =
    Assert.Equal("-", FormatHelpers.formatProbability (Nullable<float>()))

[<Fact>]
let ``formatProbability - 0.75 returns 75%`` () =
    Assert.Equal("75%", FormatHelpers.formatProbability (Nullable 0.75))

[<Fact>]
let ``formatProbability - 0.5 returns 50%`` () =
    Assert.Equal("50%", FormatHelpers.formatProbability (Nullable 0.5))

[<Theory>]
[<InlineData(0.995, ">99%")>]
[<InlineData(0.005, "<1%")>]
[<InlineData(0.99, "99%")>]
[<InlineData(0.01, "1%")>]
let ``formatProbability keeps extreme estimates distinct from exact outcomes`` probability expected =
    Assert.Equal(expected, FormatHelpers.formatProbability (Nullable probability))

[<Fact>]
let ``formatProbability - 1.0 returns 100%`` () =
    Assert.Equal("100%", FormatHelpers.formatProbability (Nullable 1.0))

[<Fact>]
let ``formatProbability - 0.0 returns 0%`` () =
    Assert.Equal("0%", FormatHelpers.formatProbability (Nullable 0.0))

[<Theory>]
[<InlineData(0.0, "<1%")>]
[<InlineData(0.0005, "<1%")>]
[<InlineData(0.005, "<1%")>]
[<InlineData(0.0099, "<1%")>]
[<InlineData(0.9901, ">99%")>]
[<InlineData(0.995, ">99%")>]
[<InlineData(0.9995, ">99%")>]
[<InlineData(1.0, ">99%")>]
let ``Playoff extreme estimates use bounds instead of rounding to certainty`` probability expected =
    Assert.Equal(expected, FormatHelpers.formatPlayoffOutlook false probability)

[<Theory>]
[<InlineData(0.01)>]
[<InlineData(0.02)>]
[<InlineData(0.635)>]
[<InlineData(0.98)>]
[<InlineData(0.99)>]
let ``Ordinary playoff estimates retain percentage formatting`` probability =
    Assert.Equal($"{probability:P0}", FormatHelpers.formatPlayoffOutlook false probability)

[<Theory>]
[<InlineData(0.0, "Missed playoffs")>]
[<InlineData(1.0, "Clinched playoffs")>]
let ``Confirmed outcomes use qualification labels instead of percentages`` probability expected =
    Assert.Equal(expected, FormatHelpers.formatPlayoffOutlook true probability)

[<Theory>]
[<InlineData(1)>]
[<InlineData(5)>]
[<InlineData(25)>]
let ``Poll ranks are superscripts labeled with the poll they come from`` rank =
    for poll, label in [ backend.Models.CollegePoll.Ap, "AP"; backend.Models.CollegePoll.Cfp, "CFP" ] do
        let node = FormatHelpers.pollRank (Nullable poll) (Nullable rank) |> List.exactlyOne
        let html = Falco.Markup.XmlNodeRenderer.renderNode node
        Assert.StartsWith("<sup ", html)
        Assert.Contains($"aria-label=\"{label} rank {rank}\"", html)
        Assert.Contains($"title=\"{label} rank {rank}\"", html)
        Assert.EndsWith($">{rank}</sup>", html)

[<Theory>]
[<InlineData(0)>]
[<InlineData(-1)>]
[<InlineData(26)>]
let ``Only valid Top 25 positions produce badges`` rank =
    Assert.Empty(FormatHelpers.pollRank (Nullable backend.Models.CollegePoll.Cfp) (Nullable rank))
    Assert.Empty(FormatHelpers.pollRank (Nullable backend.Models.CollegePoll.Ap) (Nullable<int>()))

[<Fact>]
let ``A rank without a poll produces no badge`` () =
    Assert.Empty(FormatHelpers.pollRank (Nullable<backend.Models.CollegePoll>()) (Nullable 5))

[<Theory>]
[<InlineData("regular")>]
[<InlineData("unknown")>]
[<InlineData(null)>]
let ``Routine college context adds no card rows`` competition =
    let game = backend.Models.Game(Id = "1", Sport = "cfb", HomeTeam = "Oregon", HomeTeamAbbreviation = "ORE",
                                   AwayTeam = "Boise State", AwayTeamAbbreviation = "BOIS",
                                   CollegeCompetition = competition, HomePollRank = Nullable 5, RankPoll = Nullable backend.Models.CollegePoll.Ap, HomeFpi = Nullable 25.0)
    Assert.Empty(FormatHelpers.collegeContext game)

[<Theory>]
[<InlineData(GameStatus.SCHEDULED, false)>]
[<InlineData(GameStatus.IN_PROGRESS, true)>]
[<InlineData(GameStatus.FINAL, false)>]
[<InlineData(GameStatus.CANCELED, false)>]
let ``College live probability warnings follow status in both SSE surfaces`` status showWarning =
    let update = GameUpdate(GameId = "cfb-1", Sport = Sport.CFB, Status = status, HomeTeam = "ORE", AwayTeam = "BOIS",
                            HomeScore = Nullable 7, AwayScore = Nullable 3)
    let card = SseFragmentRenderer.renderGameCardFragments update
    let detail = SseFragmentRenderer.renderDetailFragments update
    for html in [card; detail] do
        Assert.Equal(showWarning, html.Contains("Live probability unavailable"))
        Assert.DoesNotContain("50%", html)
        if status = GameStatus.SCHEDULED then Assert.DoesNotContain("Excitement unavailable", html)
    Assert.DoesNotContain("id=\"scores-cfb-1\"", card)
    Assert.Contains("id=\"away-score-cfb-1\"", card)
    Assert.Contains("id=\"home-score-cfb-1\"", card)
    Assert.Contains(">7</span>", card)
    Assert.Contains(">3</span>", card)

[<Fact>]
let ``Pro league SSE score rendering is unchanged`` () =
    let update = GameUpdate(GameId = "nfl-1", Sport = Sport.NFL, Status = GameStatus.IN_PROGRESS, HomeTeam = "KC", AwayTeam = "BUF")
    let html = SseFragmentRenderer.renderGameCardFragments update
    Assert.Contains("id=\"scores-nfl-1\"", html)
    Assert.DoesNotContain("id=\"home-score-nfl-1\"", html)

[<Fact>]
let ``formatExcitement - null returns dash`` () =
    Assert.Equal("-", FormatHelpers.formatExcitement (Nullable<float>()))

[<Fact>]
let ``formatExcitement - rounds to integer`` () =
    Assert.Equal("75", FormatHelpers.formatExcitement (Nullable 74.6))
    Assert.Equal("74", FormatHelpers.formatExcitement (Nullable 74.4))

[<Fact>]
let ``getExcitementLevel - null is Low`` () =
    Assert.Equal(FormatHelpers.Low, FormatHelpers.getExcitementLevel (Nullable<float>()))

[<Fact>]
let ``getExcitementLevel - under 30 is Low`` () =
    Assert.Equal(FormatHelpers.Low, FormatHelpers.getExcitementLevel (Nullable 29.0))

[<Fact>]
let ``getExcitementLevel - 30 to 59 is Medium`` () =
    Assert.Equal(FormatHelpers.Medium, FormatHelpers.getExcitementLevel (Nullable 30.0))
    Assert.Equal(FormatHelpers.Medium, FormatHelpers.getExcitementLevel (Nullable 59.0))

[<Fact>]
let ``getExcitementLevel - 60 and above is High`` () =
    Assert.Equal(FormatHelpers.High, FormatHelpers.getExcitementLevel (Nullable 60.0))
    Assert.Equal(FormatHelpers.High, FormatHelpers.getExcitementLevel (Nullable 100.0))

[<Fact>]
let ``excitementLevelClass returns correct CSS classes`` () =
    Assert.Equal("excitement-high", FormatHelpers.excitementLevelClass FormatHelpers.High)
    Assert.Equal("excitement-medium", FormatHelpers.excitementLevelClass FormatHelpers.Medium)
    Assert.Equal("excitement-low", FormatHelpers.excitementLevelClass FormatHelpers.Low)

[<Fact>]
let ``formatHype - null returns dash`` () =
    Assert.Equal("-", FormatHelpers.formatHype (Nullable<float>()))

[<Fact>]
let ``formatHype - rounds to integer`` () =
    Assert.Equal("85", FormatHelpers.formatHype (Nullable 85.3))

[<Fact>]
let ``getHypeLevel - null is HypeLow`` () =
    Assert.Equal(FormatHelpers.HypeLow, FormatHelpers.getHypeLevel (Nullable<float>()))

[<Fact>]
let ``getHypeLevel - under 40 is HypeLow`` () =
    Assert.Equal(FormatHelpers.HypeLow, FormatHelpers.getHypeLevel (Nullable 39.0))

[<Fact>]
let ``getHypeLevel - 40 to 69 is HypeMedium`` () =
    Assert.Equal(FormatHelpers.HypeMedium, FormatHelpers.getHypeLevel (Nullable 40.0))
    Assert.Equal(FormatHelpers.HypeMedium, FormatHelpers.getHypeLevel (Nullable 69.0))

[<Fact>]
let ``getHypeLevel - 70 and above is HypeHigh`` () =
    Assert.Equal(FormatHelpers.HypeHigh, FormatHelpers.getHypeLevel (Nullable 70.0))

[<Fact>]
let ``formatGameClock - null period returns empty`` () =
    Assert.Equal("", FormatHelpers.formatGameClock (Nullable<int>()) null "NBA")

[<Fact>]
let ``formatGameClock - MLB innings`` () =
    Assert.Equal("Inning 1", FormatHelpers.formatGameClock (Nullable 1) null "MLB")
    Assert.Equal("Inning 9", FormatHelpers.formatGameClock (Nullable 9) null "MLB")
    Assert.Equal("Extra 10", FormatHelpers.formatGameClock (Nullable 10) null "MLB")

[<Fact>]
let ``formatGameClock - NHL periods`` () =
    Assert.Equal("P1", FormatHelpers.formatGameClock (Nullable 1) "0:00" "NHL")
    Assert.Equal("P2 5:30", FormatHelpers.formatGameClock (Nullable 2) "5:30" "NHL")
    Assert.Equal("OT", FormatHelpers.formatGameClock (Nullable 4) null "NHL")
    Assert.Equal("2OT", FormatHelpers.formatGameClock (Nullable 5) null "NHL")

[<Fact>]
let ``formatGameClock - NBA quarters`` () =
    Assert.Equal("Q1 10:00", FormatHelpers.formatGameClock (Nullable 1) "10:00" "NBA")
    Assert.Equal("Q4", FormatHelpers.formatGameClock (Nullable 4) null "NBA")
    Assert.Equal("OT1", FormatHelpers.formatGameClock (Nullable 5) null "NBA")

[<Fact>]
let ``formatGameClock - NFL quarters`` () =
    Assert.Equal("Q2 2:00", FormatHelpers.formatGameClock (Nullable 2) "2:00" "NFL")
    Assert.Equal("OT1", FormatHelpers.formatGameClock (Nullable 5) null "NFL")

[<Fact>]
let ``getStatusLabel - all statuses`` () =
    Assert.Equal("Live", FormatHelpers.getStatusLabel GameStatus.IN_PROGRESS)
    Assert.Equal("Scheduled", FormatHelpers.getStatusLabel GameStatus.SCHEDULED)
    Assert.Equal("Final", FormatHelpers.getStatusLabel GameStatus.FINAL)
    Assert.Equal("Canceled", FormatHelpers.getStatusLabel GameStatus.CANCELED)

[<Fact>]
let ``getStatusClass - all statuses`` () =
    Assert.Equal("status-live animate-live-pulse", FormatHelpers.getStatusClass GameStatus.IN_PROGRESS)
    Assert.Equal("status-scheduled", FormatHelpers.getStatusClass GameStatus.SCHEDULED)
    Assert.Equal("status-final", FormatHelpers.getStatusClass GameStatus.FINAL)
    Assert.Equal("status-final", FormatHelpers.getStatusClass GameStatus.CANCELED)

[<Fact>]
let ``formatMoneyline - positive odds get plus prefix`` () =
    Assert.Equal("+270", FormatHelpers.formatMoneyline 270)

[<Fact>]
let ``formatMoneyline - negative odds stay negative`` () =
    Assert.Equal("-340", FormatHelpers.formatMoneyline -340)

[<Fact>]
let ``formatSpread - positive spread`` () =
    Assert.Equal("CHI +3.5", FormatHelpers.formatSpread 3.5 "CHI")

[<Fact>]
let ``formatSpread - negative spread`` () =
    Assert.Equal("LAL -7", FormatHelpers.formatSpread -7.0 "LAL")

[<Fact>]
let ``formatLapCount - null completed laps`` () =
    Assert.Equal("57 laps", FormatHelpers.formatLapCount (Nullable<int>()) 57)

[<Fact>]
let ``formatLapCount - with completed laps`` () =
    Assert.Equal("Lap 23/57", FormatHelpers.formatLapCount (Nullable 23) 57)

[<Fact>]
let ``formatGap - null returns dash`` () =
    Assert.Equal("-", FormatHelpers.formatGap (Nullable<float>()))

[<Fact>]
let ``formatGap - zero is Leader`` () =
    Assert.Equal("Leader", FormatHelpers.formatGap (Nullable 0.0))

[<Fact>]
let ``formatGap - positive gap`` () =
    Assert.Equal("+1.234s", FormatHelpers.formatGap (Nullable 1.234))

[<Fact>]
let ``formatTrackStatus - known statuses`` () =
    Assert.Equal("Green Flag", FormatHelpers.formatTrackStatus "green")
    Assert.Equal("Yellow Flag", FormatHelpers.formatTrackStatus "Yellow")
    Assert.Equal("Safety Car", FormatHelpers.formatTrackStatus "SC")
    Assert.Equal("Virtual Safety Car", FormatHelpers.formatTrackStatus "vsc")
    Assert.Equal("Red Flag", FormatHelpers.formatTrackStatus "Red")

[<Fact>]
let ``formatTrackStatus - null or empty`` () =
    Assert.Equal("", FormatHelpers.formatTrackStatus null)
    Assert.Equal("", FormatHelpers.formatTrackStatus "")

[<Fact>]
let ``formatTrackStatus - unknown passes through`` () =
    Assert.Equal("Custom", FormatHelpers.formatTrackStatus "Custom")

[<Fact>]
let ``formatLapTimeFixed - null returns dash`` () =
    Assert.Equal("-", FormatHelpers.formatLapTimeFixed (Nullable<float>()))

[<Fact>]
let ``formatLapTimeFixed - formats minutes and seconds`` () =
    Assert.Equal("1:23.456", FormatHelpers.formatLapTimeFixed (Nullable 83.456))

[<Fact>]
let ``formatLapTimeFixed - pads seconds under 10`` () =
    Assert.Equal("1:05.123", FormatHelpers.formatLapTimeFixed (Nullable 65.123))

[<Fact>]
let ``hypeLevelClass returns correct CSS classes`` () =
    Assert.Equal("hype-high", FormatHelpers.hypeLevelClass FormatHelpers.HypeHigh)
    Assert.Equal("hype-medium", FormatHelpers.hypeLevelClass FormatHelpers.HypeMedium)
    Assert.Equal("hype-low", FormatHelpers.hypeLevelClass FormatHelpers.HypeLow)

[<Fact>]
let ``formatScheduledTime formats correctly`` () =
    let dt = DateTime(2026, 1, 15, 19, 30, 0)
    Assert.Equal("Jan 15, 7:30 PM", FormatHelpers.formatScheduledTime dt)

[<Fact>]
let ``formatScheduledTime morning game`` () =
    let dt = DateTime(2026, 9, 7, 10, 0, 0)
    Assert.Equal("Sep 7, 10:00 AM", FormatHelpers.formatScheduledTime dt)

[<Fact>]
let ``formatScheduledTimeFromIso valid ISO string`` () =
    let result = FormatHelpers.formatScheduledTimeFromIso "2026-01-15T19:30:00"
    Assert.Equal("Jan 15, 7:30 PM", result)

[<Fact>]
let ``formatScheduledTimeFromIso invalid string returns original`` () =
    let result = FormatHelpers.formatScheduledTimeFromIso "not-a-date"
    Assert.Equal("not-a-date", result)

[<Fact>]
let ``formatLapTime - null returns dash`` () =
    Assert.Equal("-", FormatHelpers.formatLapTime (Nullable<float>()))

[<Fact>]
let ``formatLapTime - formats minutes and seconds`` () =
    let result = FormatHelpers.formatLapTime (Nullable 83.456)
    Assert.Contains("1:", result)
    Assert.Contains("23.456", result)
