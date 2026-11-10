namespace Backend.Rendering

open Falco.Markup
open backend.GraphQL.Types

module Layout =

    let private scriptReleaseVersion = "2026-11-07-plain-css"
    let private styleReleaseVersion = "2026-11-10-league-rows"
    let private stylesheets = [ "global"; "components"; "games"; "world-cup"; "motorsport" ]
    let private iconVersion = "swing-1"

    let cls = Attr.``class'``

    let private navLink (href: string) (isCurrent: bool) (children: XmlNode list) =
        Elem.a [
            Attr.href href
            cls "nav-link"
            if isCurrent then Attr.create "aria-current" "page"
        ] children

    let private navLinks (activeSport: Sport option) =
        [
            navLink "/" activeSport.IsNone [ Text.raw "All" ]
            for config in LeagueConfig.allLeagues do
                navLink config.Path (activeSport = Some config.Sport) [
                    Elem.img [ Attr.src (Logos.getLeagueLogoUrl config.Sport); Attr.alt ""; cls "nav-link-logo"; Attr.create "loading" "lazy" ]
                    Text.raw config.ShortName
                ]
        ]

    let private desktopNav (activeSport: Sport option) =
        Elem.nav [ cls "site-nav" ] (navLinks activeSport)

    let private mobileMenuButton =
        Elem.button [
            cls "menu-toggle"
            Attr.create "aria-label" "Toggle menu"
            Attr.create "onclick" "const nav = document.getElementById('mobile-nav'); nav.hidden = !nav.hidden"
        ] [
            Elem.create "svg" [ cls "menu-icon"; Attr.create "fill" "none"; Attr.create "stroke" "currentColor"; Attr.create "viewBox" "0 0 24 24"; Attr.create "stroke-width" "2" ] [
                Elem.create "path" [ Attr.create "stroke-linecap" "round"; Attr.create "stroke-linejoin" "round"; Attr.create "d" "M4 6h16M4 12h16M4 18h16" ] []
            ]
        ]

    let private mobileNav (activeSport: Sport option) =
        Elem.div [ Attr.id "mobile-nav"; Attr.create "hidden" ""; cls "mobile-nav" ] [
            Elem.nav [ cls "mobile-nav-links" ] (navLinks activeSport)
        ]

    let private header (activeSport: Sport option) =
        let config = activeSport |> Option.map LeagueConfig.getLeagueConfig
        let accentColor = config |> Option.map (fun c -> c.Colors.Primary) |> Option.defaultValue "#3b82f6"
        Elem.header [
            cls "site-header"
            Attr.style $"--header-accent: {accentColor}"
        ] [
            Elem.div [ cls "header-accent" ] []
            Elem.div [ cls "site-header-inner" ] [
                Elem.div [ cls "site-header-bar" ] [
                    Elem.div [ cls "site-identity" ] [
                        match config with
                        | Some c ->
                            Elem.img [ Attr.src (Logos.getLeagueLogoUrl c.Sport); Attr.alt c.ShortName; cls "header-league-logo" ]
                        | None -> ()
                        Elem.a [ Attr.href "/"; cls "brand" ] [
                            Elem.img [ Attr.src $"/favicon.svg?v={iconVersion}"; Attr.alt ""; Attr.create "width" "24"; Attr.create "height" "24"; cls "brand-logo" ]
                            Text.raw "Nailbiter"
                        ]
                        match config with
                        | Some c ->
                            Elem.span [ cls "site-league-name" ] [ Text.raw c.ShortName ]
                        | None -> ()
                    ]
                    desktopNav activeSport
                    mobileMenuButton
                ]
            ]
            mobileNav activeSport
        ]

    let page (title: string) (description: string) (activeSport: Sport option) (content: XmlNode list) =
        let ogUrl =
            match activeSport with
            | Some sport -> $"https://live.nailbiter.app{(LeagueConfig.getLeagueConfig sport).Path}"
            | None -> "https://live.nailbiter.app"
        Elem.html [ Attr.lang "en"; cls "dark" ] [
            Elem.head [] [
                Elem.meta [ Attr.charset "utf-8" ]
                Elem.meta [ Attr.name "viewport"; Attr.content "width=device-width, initial-scale=1.0" ]
                Elem.title [] [ Text.raw title ]
                Elem.meta [ Attr.name "description"; Attr.content description ]
                Elem.meta [ Attr.create "property" "og:title"; Attr.content title ]
                Elem.meta [ Attr.create "property" "og:description"; Attr.content description ]
                Elem.meta [ Attr.create "property" "og:type"; Attr.content "website" ]
                Elem.meta [ Attr.create "property" "og:url"; Attr.content ogUrl ]
                Elem.meta [ Attr.create "property" "og:site_name"; Attr.content "Nailbiter" ]
                Elem.meta [ Attr.name "view-transition"; Attr.content "same-origin" ]
                Elem.link [ Attr.rel "icon"; Attr.create "type" "image/x-icon"; Attr.create "sizes" "16x16 32x32 48x48"; Attr.href $"/favicon.ico?v={iconVersion}" ]
                Elem.link [ Attr.rel "icon"; Attr.create "type" "image/svg+xml"; Attr.create "sizes" "any"; Attr.href $"/favicon.svg?v={iconVersion}" ]
                Elem.link [ Attr.rel "apple-touch-icon"; Attr.create "type" "image/png"; Attr.create "sizes" "180x180"; Attr.href $"/apple-touch-icon.png?v={iconVersion}" ]
                Elem.link [ Attr.rel "manifest"; Attr.href $"/site.webmanifest?v={iconVersion}" ]
                for sheet in stylesheets do
                    Elem.link [ Attr.rel "stylesheet"; Attr.href $"/css/{sheet}.css?v={styleReleaseVersion}" ]
                Elem.script [ Attr.create "type" "module"; Attr.src "https://cdn.jsdelivr.net/gh/starfederation/datastar@v1.0.4/bundles/datastar.js" ] []
                Elem.script [ Attr.create "type" "module"; Attr.src $"/js/nailbiter-components.js?v={scriptReleaseVersion}" ] []
                Elem.script [ Attr.create "type" "module"; Attr.src $"/js/live-sync.js?v={scriptReleaseVersion}" ] []
            ]
            Elem.body [] [
                Elem.div [ Attr.id "live-reconnect-warning"; Attr.create "role" "status"; Attr.create "hidden" ""; cls "reconnect-warning" ] [
                    Text.raw "Reconnecting—data may be outdated"
                ]
                header activeSport
                Elem.main [ Attr.id "main-content" ] content
            ]
        ]

    let renderPage title description activeSport content =
        page title description activeSport content
        |> XmlNodeRenderer.renderNode
