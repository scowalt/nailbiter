module NoOddsContrastTests

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open AngleSharp.Html.Parser
open Xunit
open backend.GraphQL.Types
open backend.Models
open backend.Services
open Backend.Rendering

let private message = "Live probability unavailable"

let private css name = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Css", $"{name}.css"))

let private declarations (block: string) =
    Regex.Matches(block, @"(--[\w-]+)\s*:\s*([^;]+);")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value.Trim())
    |> Map.ofSeq

let private ruleBody (source: string) (selectorPattern: string) =
    let m = Regex.Match(source, selectorPattern + @"\s*\{([^{}]*)\}")
    Assert.True(m.Success, $"No rule matches {selectorPattern}")
    m.Groups[1].Value

let private themes =
    let globalCss = css "global"
    let dark = declarations (ruleBody globalCss @"@layer theme\s*\{\s*:root")
    let light = declarations (ruleBody globalCss @"@media \(prefers-color-scheme: light\)\s*\{\s*:root")
    [ "dark", dark; "light", Map.fold (fun tokens name value -> Map.add name value tokens) dark light ]

let private noteClassColors =
    let gamesCss = css "games"
    Regex.Matches(gamesCss, @"([^{}]+)\{([^{}]*)\}")
    |> Seq.collect (fun rule ->
        let colorDeclaration = Regex.Match(rule.Groups[2].Value, @"(?<![-\w])color\s*:\s*var\((--[\w-]+)\)\s*;")
        rule.Groups[1].Value.Split(',')
        |> Seq.map (fun selector -> selector.Trim())
        |> Seq.filter (fun selector -> Regex.IsMatch(selector, @"^\.[\w-]+$") && colorDeclaration.Success)
        |> Seq.map (fun selector -> selector.TrimStart('.'), colorDeclaration.Groups[1].Value))
    |> Map.ofSeq

let private luminance (hex: string) =
    let digits = hex.TrimStart('#')
    let full = if digits.Length = 3 then String.Concat(digits |> Seq.map (fun c -> String(c, 2))) else digits
    Assert.True(full.Length = 6, $"{hex} is not a hex color")
    let channel start =
        let value = float (Convert.ToInt32(full.Substring(start, 2), 16)) / 255.0
        if value <= 0.04045 then value / 12.92 else ((value + 0.055) / 1.055) ** 2.4
    0.2126 * channel 0 + 0.7152 * channel 2 + 0.0722 * channel 4

let private contrast a b =
    let la, lb = luminance a, luminance b
    (max la lb + 0.05) / (min la lb + 0.05)

let private surfaces =
    [ "--color-bg-primary"; "--color-bg-secondary"; "--color-bg-card"; "--color-bg-card-hover"; "--color-bg-card-raised"; "--color-bg-card-raised-hover" ]

let private noteClasses (html: string) =
    use doc = HtmlParser().ParseDocument(html)
    let note =
        doc.All
        |> Seq.filter (fun element -> element.TextContent.Trim() = message && element.Children |> Seq.forall (fun child -> child.TextContent.Trim() <> message))
        |> Seq.exactlyOne
    note.ClassList |> Seq.toList

let private liveCollegeUpdate () =
    GameUpdate(GameId = "cfb-1", Sport = Sport.CFB, Status = GameStatus.IN_PROGRESS, HomeTeam = "ORE", AwayTeam = "BOIS",
               HomeScore = Nullable 7, AwayScore = Nullable 3)

let private liveCollegeDetailPage () =
    let game =
        Game(Id = "cfb-1", Sport = "cfb", Status = "in_progress", ScheduledStart = DateTime(2026, 10, 1, 23, 0, 0, DateTimeKind.Utc),
             AwayTeam = "Boise State", AwayTeamAbbreviation = "BOIS", HomeTeam = "Oregon", HomeTeamAbbreviation = "ORE",
             AwayScore = Nullable 3, HomeScore = Nullable 7)
    GameDetailRenderer.Render game (List<OddsSnapshot>()) None (HypeComponents()) (Nullable()) None None
    |> List.map Falco.Markup.XmlNodeRenderer.renderNode
    |> String.concat ""

let private noOddsSurfaces () =
    [ "game card fragment", SseFragmentRenderer.renderGameCardFragments (liveCollegeUpdate ())
      "game detail fragment", SseFragmentRenderer.renderDetailFragments (liveCollegeUpdate ())
      "game detail page", liveCollegeDetailPage () ]

[<Fact>]
let ``No-odds text meets WCAG AA contrast on every surface in both themes`` () =
    let failures =
        [ for surface, html in noOddsSurfaces () do
            let styled = noteClasses html |> List.filter noteClassColors.ContainsKey
            Assert.True(not styled.IsEmpty, $"{surface} renders the no-odds text without a class that sets its color")
            for className in styled do
                let token = noteClassColors[className]
                for theme, tokens in themes do
                    let text = tokens[token]
                    for background in surfaces do
                        let ratio = contrast text tokens[background]
                        if ratio < 4.5 then
                            yield $"{surface} .{className} {token} {text} on {background} {tokens[background]} ({theme}): {ratio:F2}:1" ]
    Assert.True(failures.IsEmpty, String.Join("\n", failures))
