module TeamColorsTests

open Xunit
open Backend.Rendering
open backend.GraphQL.Types

[<Fact>]
let ``getTeamColors - known NBA team`` () =
    let colors = TeamColors.getTeamColors Sport.NBA "LAL"
    Assert.True(colors.IsSome)
    Assert.Equal("#552583", colors.Value.Primary)
    Assert.Equal("#FDB927", colors.Value.Secondary)

[<Fact>]
let ``getTeamColors - case insensitive`` () =
    let colors = TeamColors.getTeamColors Sport.NBA "lal"
    Assert.True(colors.IsSome)
    Assert.Equal("#552583", colors.Value.Primary)

[<Fact>]
let ``getTeamColors - unknown team returns None`` () =
    let colors = TeamColors.getTeamColors Sport.NBA "ZZZ"
    Assert.True(colors.IsNone)

[<Fact>]
let ``getTeamColors - NBA aliases work`` () =
    let gs = TeamColors.getTeamColors Sport.NBA "GS"
    let gsw = TeamColors.getTeamColors Sport.NBA "GSW"
    Assert.True(gs.IsSome)
    Assert.True(gsw.IsSome)
    Assert.Equal(gs.Value.Primary, gsw.Value.Primary)

[<Fact>]
let ``getTeamColors - NFL team`` () =
    let colors = TeamColors.getTeamColors Sport.NFL "KC"
    Assert.True(colors.IsSome)
    Assert.Equal("#E31837", colors.Value.Primary)

[<Fact>]
let ``getTeamColors - NHL team`` () =
    let colors = TeamColors.getTeamColors Sport.NHL "BOS"
    Assert.True(colors.IsSome)
    Assert.Equal("#FFB81C", colors.Value.Primary)

[<Fact>]
let ``getTeamColors - MLB team`` () =
    let colors = TeamColors.getTeamColors Sport.MLB "NYY"
    Assert.True(colors.IsSome)
    Assert.Equal("#003087", colors.Value.Primary)

[<Fact>]
let ``getTeamColors - F1 constructor`` () =
    let colors = TeamColors.getTeamColors Sport.F1 "Ferrari"
    Assert.True(colors.IsSome)
    Assert.Equal("#E8002D", colors.Value.Primary)

[<Fact>]
let ``getTeamColors - NASCAR returns None`` () =
    Assert.True((TeamColors.getTeamColors Sport.NASCAR "any").IsNone)

[<Fact>]
let ``getTeamColor - returns primary`` () =
    let color = TeamColors.getTeamColor Sport.NBA "BOS"
    Assert.Equal(Some "#007A33", color)

[<Fact>]
let ``getTeamColorOrDefault - known team`` () =
    Assert.Equal("#007A33", TeamColors.getTeamColorOrDefault Sport.NBA "BOS" "#999")

[<Fact>]
let ``getTeamColorOrDefault - unknown team uses default`` () =
    Assert.Equal("#999", TeamColors.getTeamColorOrDefault Sport.NBA "ZZZ" "#999")

let private palette primary secondary : TeamColors.TeamColorPair =
    { Primary = primary; Secondary = secondary }

let private luminance (hex: string) =
    let channel start =
        let value = float (System.Convert.ToInt32(hex.Substring(start, 2), 16)) / 255.0
        if value <= 0.04045 then value / 12.92 else ((value + 0.055) / 1.055) ** 2.4
    0.2126 * channel 1 + 0.7152 * channel 3 + 0.0722 * channel 5

let private assertVisible color =
    let contrast = (luminance color + 0.05) / (luminance "#18181b" + 0.05)
    Assert.True(contrast >= 3.0, $"{color} has only {contrast:F2}:1 card contrast")

[<Fact>]
let ``matchup preserves distinct visible primary colors`` () =
    let colors = TeamColors.selectMatchupColors (palette "#007A33" "#BA9653") (palette "#CE1141" "#000000")
    Assert.Equal("#007A33", colors.Home)
    Assert.Equal("#CE1141", colors.Away)

[<Theory>]
[<InlineData("#CE1141", "#000000")>]
[<InlineData("#FFFFFF", "#FFFFFF")>]
[<InlineData("#000000", "#000000")>]
[<InlineData("#6b7280", "#6b7280")>]
[<InlineData("#123456", "#123456")>]
let ``identical palettes still yield distinct visible colors`` primary secondary =
    let p = palette primary secondary
    let colors = TeamColors.selectMatchupColors p p
    Assert.NotEqual<string>(colors.Home, colors.Away)
    assertVisible colors.Home
    assertVisible colors.Away
    Assert.Equal(colors, TeamColors.selectMatchupColors p p)

[<Fact>]
let ``pair selection may change home when away alternatives do not help`` () =
    let colors = TeamColors.selectMatchupColors (palette "#CE1141" "#007A33") (palette "#CE1141" "#CE1141")
    Assert.Equal("#007A33", colors.Home)
    Assert.Equal("#CE1141", colors.Away)

[<Theory>]
[<InlineData(Sport.MLB, "CIN", "CHC")>]
[<InlineData(Sport.MLB, "CHC", "CIN")>]
[<InlineData(Sport.NBA, "CHI", "HOU")>]
[<InlineData(Sport.NBA, "NYK", "OKC")>]
[<InlineData(Sport.NFL, "NYG", "DAL")>]
[<InlineData(Sport.NHL, "TOR", "TB")>]
let ``known matchups are distinct and visible`` sport home away =
    let colors = TeamColors.selectMatchupColors (TeamColors.getTeamColors sport home).Value (TeamColors.getTeamColors sport away).Value
    Assert.NotEqual<string>(colors.Home, colors.Away)
    assertVisible colors.Home
    assertVisible colors.Away
