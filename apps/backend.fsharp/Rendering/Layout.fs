namespace Backend.Rendering

open Falco.Markup
open backend.GraphQL.Types

module Layout =

    let private scriptReleaseVersion = "2026-10-07-highlights-consent"
    let private styleReleaseVersion = "2026-11-05-tv-channels"
    let private iconVersion = "swing-1"

    let cls = Attr.``class'``

    let private navItem (sport: Sport) (config: LeagueConfig.LeagueConfig) (activeSport: Sport option) =
        let isActive = activeSport = Some sport
        let activeClass = if isActive then " text-text-primary" else ""
        Elem.a [ Attr.href config.Path; cls $"flex items-center gap-1.5 px-2 py-1.5 text-sm text-text-muted hover:text-text-primary transition-colors rounded-md{activeClass}" ] [
            Elem.img [ Attr.src (Logos.getLeagueLogoUrl sport); Attr.alt config.ShortName; cls "w-4 h-4"; Attr.create "loading" "lazy" ]
            Text.raw config.ShortName
        ]

    let private navLinks (activeSport: Sport option) =
        let allLink =
            let isActive = activeSport.IsNone
            let activeClass = if isActive then " text-text-primary font-medium" else ""
            Elem.a [ Attr.href "/"; cls $"flex items-center gap-1.5 px-2 py-1.5 text-sm text-text-muted hover:text-text-primary transition-colors rounded-md{activeClass}" ] [
                Text.raw "All"
            ]
        [
            allLink
            yield! LeagueConfig.allLeagues |> List.map (fun c -> navItem c.Sport c activeSport)
        ]

    let private desktopNav (activeSport: Sport option) =
        Elem.nav [ cls "hidden md:flex items-center gap-1" ] (navLinks activeSport)

    let private mobileMenuButton =
        Elem.button [
            cls "md:hidden p-2 text-text-muted hover:text-text-primary transition-colors"
            Attr.create "aria-label" "Toggle menu"
            Attr.create "onclick" "document.getElementById('mobile-nav').classList.toggle('hidden')"
        ] [
            Elem.create "svg" [ cls "w-6 h-6"; Attr.create "fill" "none"; Attr.create "stroke" "currentColor"; Attr.create "viewBox" "0 0 24 24"; Attr.create "stroke-width" "2" ] [
                Elem.create "path" [ Attr.create "stroke-linecap" "round"; Attr.create "stroke-linejoin" "round"; Attr.create "d" "M4 6h16M4 12h16M4 18h16" ] []
            ]
        ]

    let private mobileNav (activeSport: Sport option) =
        Elem.div [ Attr.id "mobile-nav"; cls "hidden md:hidden border-t border-border bg-bg-primary" ] [
            Elem.nav [ cls "flex flex-wrap gap-1 px-4 py-3" ] (navLinks activeSport)
        ]

    let private header (activeSport: Sport option) =
        let config = activeSport |> Option.map LeagueConfig.getLeagueConfig
        let accentColor = config |> Option.map (fun c -> c.Colors.Primary) |> Option.defaultValue "#3b82f6"
        Elem.header [
            cls "sticky top-0 z-50 bg-bg-primary/95 backdrop-blur-sm border-b border-border"
            Attr.style $"--header-accent: {accentColor}"
        ] [
            Elem.div [ cls "header-accent" ] []
            Elem.div [ cls "max-w-7xl mx-auto px-4 sm:px-6 lg:px-8" ] [
                Elem.div [ cls "flex items-center justify-between h-14" ] [
                    Elem.div [ cls "flex items-center gap-3" ] [
                        match config with
                        | Some c ->
                            Elem.img [ Attr.src (Logos.getLeagueLogoUrl c.Sport); Attr.alt c.ShortName; cls "w-6 h-6" ]
                        | None -> ()
                        Elem.a [ Attr.href "/"; cls "flex items-center gap-2 text-lg font-semibold text-text-primary hover:text-accent transition-colors" ] [
                            Elem.img [ Attr.src $"/favicon.svg?v={iconVersion}"; Attr.alt ""; Attr.create "width" "24"; Attr.create "height" "24"; cls "w-6 h-6" ]
                            Text.raw "Nailbiter"
                        ]
                        match config with
                        | Some c ->
                            Elem.span [ cls "text-text-muted text-sm" ] [ Text.raw c.ShortName ]
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
                Elem.link [ Attr.rel "stylesheet"; Attr.href $"/css/global.css?v={styleReleaseVersion}" ]
                Elem.script [ Attr.create "type" "module"; Attr.src "https://cdn.jsdelivr.net/gh/starfederation/datastar@v1.0.4/bundles/datastar.js" ] []
                Elem.script [ Attr.create "type" "module"; Attr.src $"/js/nailbiter-components.js?v={scriptReleaseVersion}" ] []
                Elem.script [ Attr.create "type" "module"; Attr.src $"/js/live-sync.js?v={scriptReleaseVersion}" ] []
            ]
            Elem.body [ cls "bg-bg-primary text-text-primary min-h-screen" ] [
                Elem.div [ Attr.id "live-reconnect-warning"; Attr.create "role" "status"; cls "hidden fixed bottom-4 right-4 z-[100] rounded-md border border-status-warning/40 bg-bg-card px-3 py-2 text-sm text-status-warning shadow-lg" ] [
                    Text.raw "Reconnecting—data may be outdated"
                ]
                header activeSport
                Elem.main [ Attr.id "main-content" ] content
            ]
        ]

    let renderPage title description activeSport content =
        page title description activeSport content
        |> XmlNodeRenderer.renderNode
