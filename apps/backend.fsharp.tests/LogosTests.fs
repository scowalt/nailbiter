module LogosTests

open System
open System.Collections.Generic
open Xunit
open AngleSharp.Html.Parser
open Falco.Markup
open Backend.Rendering
open backend.GraphQL.Types
open backend.Models
open backend.Services

[<Fact>]
let ``getTeamLogoUrl - known NBA team`` () =
    let url = Logos.getTeamLogoUrl Sport.NBA "LAL"
    Assert.Equal(Some "https://a.espncdn.com/i/teamlogos/nba/500/scoreboard/lal.png", url)

[<Fact>]
let ``getTeamLogoUrl - case insensitive`` () =
    let url = Logos.getTeamLogoUrl Sport.NBA "lal"
    Assert.True(url.IsSome)
    Assert.Contains("lal.png", url.Value)

[<Fact>]
let ``getTeamLogoUrl - NBA alias GS/GSW both resolve`` () =
    let gs = Logos.getTeamLogoUrl Sport.NBA "GS"
    let gsw = Logos.getTeamLogoUrl Sport.NBA "GSW"
    Assert.True(gs.IsSome)
    Assert.True(gsw.IsSome)
    Assert.Equal(gs, gsw)

[<Fact>]
let ``getTeamLogoUrl - NFL team`` () =
    let url = Logos.getTeamLogoUrl Sport.NFL "KC"
    Assert.Equal(Some "https://a.espncdn.com/i/teamlogos/nfl/500/scoreboard/kc.png", url)

[<Theory>]
[<InlineData("WSH")>]
[<InlineData("WAS")>]
[<InlineData("wsh")>]
let ``getTeamLogoUrl - Washington NFL aliases resolve`` abbreviation =
    let url = Logos.getTeamLogoUrl Sport.NFL abbreviation
    Assert.Equal(Some "https://a.espncdn.com/i/teamlogos/nfl/500/scoreboard/wsh.png", url)

[<Fact>]
let ``Washington at Philadelphia game detail renders both team logos`` () =
    let game =
        Game(
            Id = "401872929",
            Sport = "nfl",
            HomeTeam = "Philadelphia Eagles",
            AwayTeam = "Washington Commanders",
            HomeTeamAbbreviation = "PHI",
            AwayTeamAbbreviation = "WSH",
            ScheduledStart = DateTime(2026, 9, 13, 20, 25, 0, DateTimeKind.Utc),
            Status = "final",
            HomeScore = Nullable(24),
            AwayScore = Nullable(22)
        )
    let html =
        GameDetailRenderer.Render game (List<OddsSnapshot>()) None (HypeComponents()) (Nullable()) None None
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""

    Assert.Contains("src=\"https://a.espncdn.com/i/teamlogos/nfl/500/scoreboard/wsh.png\" alt=\"WSH\"", html)
    Assert.Contains("src=\"https://a.espncdn.com/i/teamlogos/nfl/500/scoreboard/phi.png\" alt=\"PHI\"", html)

[<Fact>]
let ``getTeamLogoUrl - NHL team`` () =
    let url = Logos.getTeamLogoUrl Sport.NHL "BOS"
    Assert.Equal(Some "https://a.espncdn.com/i/teamlogos/nhl/500/scoreboard/bos.png", url)

[<Theory>]
[<InlineData("WSH")>]
[<InlineData("WAS")>]
[<InlineData("wsh")>]
let ``getTeamLogoUrl - Washington NHL aliases resolve`` abbreviation =
    let url = Logos.getTeamLogoUrl Sport.NHL abbreviation
    Assert.Equal(Some "https://a.espncdn.com/i/teamlogos/nhl/500/scoreboard/wsh.png", url)

[<Fact>]
let ``Washington at Carolina game detail renders both team logos`` () =
    let game =
        Game(
            Id = "401892491",
            Sport = "nhl",
            HomeTeam = "Carolina Hurricanes",
            AwayTeam = "Washington Capitals",
            HomeTeamAbbreviation = "CAR",
            AwayTeamAbbreviation = "WSH",
            ScheduledStart = DateTime(2026, 10, 13, 22, 45, 0, DateTimeKind.Utc),
            Status = "scheduled"
        )
    let html =
        GameDetailRenderer.Render game (List<OddsSnapshot>()) None (HypeComponents()) (Nullable()) None None
        |> List.map XmlNodeRenderer.renderNode
        |> String.concat ""
    use doc = HtmlParser().ParseDocument(html)
    let awayLogo = doc.QuerySelector("#detail-scoreboard img[alt='WSH']")
    let homeLogo = doc.QuerySelector("#detail-scoreboard img[alt='CAR']")

    Assert.NotNull(awayLogo)
    Assert.NotNull(homeLogo)
    Assert.Equal("https://a.espncdn.com/i/teamlogos/nhl/500/scoreboard/wsh.png", awayLogo.GetAttribute("src"))
    Assert.Equal("https://a.espncdn.com/i/teamlogos/nhl/500/scoreboard/car.png", homeLogo.GetAttribute("src"))

[<Fact>]
let ``getTeamLogoUrl - MLB team`` () =
    let url = Logos.getTeamLogoUrl Sport.MLB "NYY"
    Assert.Equal(Some "https://a.espncdn.com/i/teamlogos/mlb/500/scoreboard/nyy.png", url)

[<Fact>]
let ``getTeamLogoUrl - unknown team returns None`` () =
    Assert.True((Logos.getTeamLogoUrl Sport.NBA "ZZZ").IsNone)

[<Fact>]
let ``getTeamLogoUrl - F1 returns None`` () =
    Assert.True((Logos.getTeamLogoUrl Sport.F1 "Ferrari").IsNone)

[<Fact>]
let ``getTeamLogoUrl - WAS alias maps to wsh`` () =
    let nfl = Logos.getTeamLogoUrl Sport.NFL "WAS"
    Assert.True(nfl.IsSome)
    Assert.Contains("wsh.png", nfl.Value)

[<Fact>]
let ``getLeagueLogoUrl - all sports return paths`` () =
    Assert.Equal("/logos/nba.png", Logos.getLeagueLogoUrl Sport.NBA)
    Assert.Equal("/logos/nfl.png", Logos.getLeagueLogoUrl Sport.NFL)
    Assert.Equal("/logos/nhl.png", Logos.getLeagueLogoUrl Sport.NHL)
    Assert.Equal("/logos/mlb.png", Logos.getLeagueLogoUrl Sport.MLB)
    Assert.Equal("/logos/f1.png", Logos.getLeagueLogoUrl Sport.F1)
    Assert.Equal("/logos/nascar.png", Logos.getLeagueLogoUrl Sport.NASCAR)
    Assert.Equal("/logos/indycar.png", Logos.getLeagueLogoUrl Sport.INDYCAR)
