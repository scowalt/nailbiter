module LeagueConfigTests

open Xunit
open Backend.Rendering
open backend.GraphQL.Types

[<Fact>]
let ``getLeagueConfig - NBA`` () =
    let config = LeagueConfig.getLeagueConfig Sport.NBA
    Assert.Equal("NBA Basketball", config.Name)
    Assert.Equal("NBA", config.ShortName)
    Assert.Equal("/nba", config.Path)
    Assert.Equal("excitement", config.DefaultSort)
    Assert.True(config.DisplayOptions.ShowPlayoffOdds)
    Assert.True(config.DisplayOptions.ShowBracket)
    Assert.Equal(LeagueConfig.Quarters, config.DisplayOptions.ClockFormat)

[<Fact>]
let ``getLeagueConfig - NFL`` () =
    let config = LeagueConfig.getLeagueConfig Sport.NFL
    Assert.Equal("NFL Football", config.Name)
    Assert.Equal("/nfl", config.Path)
    Assert.False(config.DisplayOptions.ShowBracket)

[<Fact>]
let ``getLeagueConfig - NHL`` () =
    let config = LeagueConfig.getLeagueConfig Sport.NHL
    Assert.Equal("NHL Hockey", config.Name)
    Assert.Equal(LeagueConfig.Periods, config.DisplayOptions.ClockFormat)
    Assert.True(config.DisplayOptions.ShowBracket)

[<Fact>]
let ``getLeagueConfig - MLB`` () =
    let config = LeagueConfig.getLeagueConfig Sport.MLB
    Assert.Equal("MLB Baseball", config.Name)
    Assert.Equal(LeagueConfig.Innings, config.DisplayOptions.ClockFormat)

[<Fact>]
let ``getLeagueConfig - F1`` () =
    let config = LeagueConfig.getLeagueConfig Sport.F1
    Assert.Equal("Formula 1", config.Name)
    Assert.False(config.DisplayOptions.ShowPlayoffOdds)
    Assert.False(config.DisplayOptions.ShowBracket)

[<Fact>]
let ``getLeagueConfig - NASCAR`` () =
    let config = LeagueConfig.getLeagueConfig Sport.NASCAR
    Assert.Equal("NASCAR Cup Series", config.Name)
    Assert.Equal("/nascar", config.Path)

[<Fact>]
let ``getLeagueConfig - INDYCAR`` () =
    let config = LeagueConfig.getLeagueConfig Sport.INDYCAR
    Assert.Equal("IndyCar Series", config.Name)
    Assert.Equal("/indycar", config.Path)

[<Fact>]
let ``getLeagueConfig - World Cup`` () =
    let config = LeagueConfig.getLeagueConfig Sport.WORLD_CUP
    Assert.Equal("FIFA World Cup", config.Name)
    Assert.Equal("World Cup", config.ShortName)
    Assert.Equal("/world-cup", config.Path)
    Assert.Equal(LeagueConfig.Halves, config.DisplayOptions.ClockFormat)

[<Fact>]
let ``getLeagueByPath - valid path`` () =
    let config = LeagueConfig.getLeagueByPath "/nba"
    Assert.True(config.IsSome)
    Assert.Equal(Sport.NBA, config.Value.Sport)

[<Fact>]
let ``getLeagueByPath - invalid path`` () =
    Assert.True((LeagueConfig.getLeagueByPath "/cricket").IsNone)

[<Fact>]
let ``allLeagues - excludes World Cup after tournament ends`` () =
    Assert.Equal(8, LeagueConfig.allLeagues.Length)
    Assert.Contains(LeagueConfig.allLeagues, fun config -> config.Sport = Sport.CFB && not config.DisplayOptions.ShowBracket)
    Assert.DoesNotContain(LeagueConfig.allLeagues, fun config -> config.Sport = Sport.WORLD_CUP)

[<Fact>]
let ``all configs have non-empty colors`` () =
    for config in LeagueConfig.allLeagues do
        Assert.False(System.String.IsNullOrEmpty(config.Colors.Primary), $"{config.Name} has empty primary color")
        Assert.False(System.String.IsNullOrEmpty(config.Meta.Description), $"{config.Name} has empty description")
        Assert.False(System.String.IsNullOrEmpty(config.Meta.Emoji), $"{config.Name} has empty emoji")
