module LayoutTests

open System
open System.Text.RegularExpressions
open Xunit
open Backend.Rendering
open backend.GraphQL.Types

let private pageContexts = None :: (Enum.GetValues<Sport>() |> Array.map Some |> Array.toList)

[<Fact>]
let ``page uses one release version for local scripts`` () =
    let html = Layout.renderPage "Nailbiter" "Live games" None []

    Assert.Contains("/js/nailbiter-components.js?v=2026-11-07-plain-css", html)
    Assert.Contains("/js/live-sync.js?v=2026-11-07-plain-css", html)

[<Fact>]
let ``page links the layered stylesheets in cascade order`` () =
    let html = Layout.renderPage "Nailbiter" "Live games" None []
    let sheets =
        Regex.Matches(html, "<link rel=\"stylesheet\" href=\"([^\"]+)\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Seq.toList

    Assert.Equal<string list>(
        [ for name in [ "global"; "components"; "games"; "world-cup"; "motorsport" ] -> $"/css/{name}.css?v=2026-11-07-plain-css" ],
        sheets)

[<Fact>]
let ``every page declares the same browser and home screen icons`` () =
    let expectedLinks = [
        "icon", "image/x-icon", "16x16 32x32 48x48", "/favicon.ico?v=swing-1"
        "icon", "image/svg+xml", "any", "/favicon.svg?v=swing-1"
        "apple-touch-icon", "image/png", "180x180", "/apple-touch-icon.png?v=swing-1"
    ]

    for activeSport in pageContexts do
        let html = Layout.renderPage "Nailbiter" "Live games" activeSport []
        let head = html.Split("</head>")[0]
        let links = Regex.Matches(head, "<link\\b[^>]*>") |> Seq.map (fun m -> m.Value) |> Seq.toList

        for rel, contentType, sizes, href in expectedLinks do
            let matching = links |> List.filter (fun link -> link.Contains($"href=\"{href}\""))
            let link = Assert.Single(matching)
            Assert.Contains($"rel=\"{rel}\"", link)
            Assert.Contains($"type=\"{contentType}\"", link)
            Assert.Contains($"sizes=\"{sizes}\"", link)

        Assert.Contains(links, fun link -> link.Contains("rel=\"manifest\"") && link.Contains("href=\"/site.webmanifest?v=swing-1\""))

[<Fact>]
let ``home link carries a decorative brand mark on every page`` () =
    for activeSport in pageContexts do
        let html = Layout.renderPage "Nailbiter" "Live games" activeSport []
        let homeLink =
            Regex.Matches(html, "<a\\b[^>]*href=\"/\"[^>]*>.*?</a>", RegexOptions.Singleline)
            |> Seq.map (fun m -> m.Value)
            |> Seq.find (fun link -> link.Contains("Nailbiter"))

        Assert.Contains("src=\"/favicon.svg?v=swing-1\"", homeLink)
        Assert.Contains("alt=\"\"", homeLink)
        Assert.Contains("width=\"24\"", homeLink)
        Assert.Contains("height=\"24\"", homeLink)

[<Fact>]
let ``brand mark does not replace league identities`` () =
    for sport in Enum.GetValues<Sport>() do
        let html = Layout.renderPage "Nailbiter" "Live games" (Some sport) []
        let config = LeagueConfig.getLeagueConfig sport
        Assert.Contains($"src=\"{Logos.getLeagueLogoUrl sport}\"", html)
        Assert.Contains(config.ShortName, html)
