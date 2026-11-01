namespace Backend.Rendering

open System
open System.Collections.Generic
open backend.GraphQL.Types

module TeamColors =

    type TeamColorPair = { Primary: string; Secondary: string }

    let private nbaColors =
        dict [
            "ATL", { Primary = "#E03A3E"; Secondary = "#C1D32F" }
            "BOS", { Primary = "#007A33"; Secondary = "#BA9653" }
            "BKN", { Primary = "#000000"; Secondary = "#FFFFFF" }
            "CHA", { Primary = "#1D1160"; Secondary = "#00788C" }
            "CHI", { Primary = "#CE1141"; Secondary = "#000000" }
            "CLE", { Primary = "#860038"; Secondary = "#FDBB30" }
            "DAL", { Primary = "#00538C"; Secondary = "#002B5E" }
            "DEN", { Primary = "#0E2240"; Secondary = "#FEC524" }
            "DET", { Primary = "#C8102E"; Secondary = "#1D42BA" }
            "GS", { Primary = "#1D428A"; Secondary = "#FFC72C" }
            "GSW", { Primary = "#1D428A"; Secondary = "#FFC72C" }
            "HOU", { Primary = "#CE1141"; Secondary = "#000000" }
            "IND", { Primary = "#002D62"; Secondary = "#FDBB30" }
            "LAC", { Primary = "#C8102E"; Secondary = "#1D428A" }
            "LAL", { Primary = "#552583"; Secondary = "#FDB927" }
            "MEM", { Primary = "#5D76A9"; Secondary = "#12173F" }
            "MIA", { Primary = "#98002E"; Secondary = "#F9A01B" }
            "MIL", { Primary = "#00471B"; Secondary = "#EEE1C6" }
            "MIN", { Primary = "#0C2340"; Secondary = "#236192" }
            "NO", { Primary = "#0C2340"; Secondary = "#C8102E" }
            "NOP", { Primary = "#0C2340"; Secondary = "#C8102E" }
            "NY", { Primary = "#F58426"; Secondary = "#006BB6" }
            "NYK", { Primary = "#F58426"; Secondary = "#006BB6" }
            "OKC", { Primary = "#007AC1"; Secondary = "#EF3B24" }
            "ORL", { Primary = "#0077C0"; Secondary = "#000000" }
            "PHI", { Primary = "#006BB6"; Secondary = "#ED174C" }
            "PHX", { Primary = "#1D1160"; Secondary = "#E56020" }
            "POR", { Primary = "#E03A3E"; Secondary = "#000000" }
            "SA", { Primary = "#C4CED4"; Secondary = "#000000" }
            "SAC", { Primary = "#5A2D81"; Secondary = "#63727A" }
            "SAS", { Primary = "#C4CED4"; Secondary = "#000000" }
            "TOR", { Primary = "#CE1141"; Secondary = "#000000" }
            "UTA", { Primary = "#002B5C"; Secondary = "#F9A01B" }
            "UTAH", { Primary = "#002B5C"; Secondary = "#F9A01B" }
            "WAS", { Primary = "#002B5C"; Secondary = "#E31837" }
            "WSH", { Primary = "#002B5C"; Secondary = "#E31837" }
        ]

    let private nflColors =
        dict [
            "ARI", { Primary = "#97233F"; Secondary = "#FFB612" }
            "ATL", { Primary = "#A71930"; Secondary = "#000000" }
            "BAL", { Primary = "#241773"; Secondary = "#000000" }
            "BUF", { Primary = "#00338D"; Secondary = "#C60C30" }
            "CAR", { Primary = "#0085CA"; Secondary = "#101820" }
            "CHI", { Primary = "#0B162A"; Secondary = "#C83803" }
            "CIN", { Primary = "#FB4F14"; Secondary = "#000000" }
            "CLE", { Primary = "#311D00"; Secondary = "#FF3C00" }
            "DAL", { Primary = "#003594"; Secondary = "#869397" }
            "DEN", { Primary = "#FB4F14"; Secondary = "#002244" }
            "DET", { Primary = "#0076B6"; Secondary = "#B0B7BC" }
            "GB", { Primary = "#203731"; Secondary = "#FFB612" }
            "HOU", { Primary = "#03202F"; Secondary = "#A71930" }
            "IND", { Primary = "#002C5F"; Secondary = "#A2AAAD" }
            "JAX", { Primary = "#006778"; Secondary = "#D7A22A" }
            "KC", { Primary = "#E31837"; Secondary = "#FFB81C" }
            "LV", { Primary = "#000000"; Secondary = "#A5ACAF" }
            "LAC", { Primary = "#0080C6"; Secondary = "#FFC20E" }
            "LAR", { Primary = "#003594"; Secondary = "#FFA300" }
            "MIA", { Primary = "#008E97"; Secondary = "#FC4C02" }
            "MIN", { Primary = "#4F2683"; Secondary = "#FFC62F" }
            "NE", { Primary = "#002244"; Secondary = "#C60C30" }
            "NO", { Primary = "#D3BC8D"; Secondary = "#101820" }
            "NYG", { Primary = "#0B2265"; Secondary = "#A71930" }
            "NYJ", { Primary = "#125740"; Secondary = "#FFFFFF" }
            "PHI", { Primary = "#004C54"; Secondary = "#A5ACAF" }
            "PIT", { Primary = "#FFB612"; Secondary = "#101820" }
            "SF", { Primary = "#AA0000"; Secondary = "#B3995D" }
            "SEA", { Primary = "#002244"; Secondary = "#69BE28" }
            "TB", { Primary = "#D50A0A"; Secondary = "#FF7900" }
            "TEN", { Primary = "#4B92DB"; Secondary = "#0C2340" }
            "WAS", { Primary = "#5A1414"; Secondary = "#FFB612" }
            "WSH", { Primary = "#5A1414"; Secondary = "#FFB612" }
        ]

    let private nhlColors =
        dict [
            "ANA", { Primary = "#F47A38"; Secondary = "#B9975B" }
            "ARI", { Primary = "#8C2633"; Secondary = "#E2D6B5" }
            "BOS", { Primary = "#FFB81C"; Secondary = "#000000" }
            "BUF", { Primary = "#002654"; Secondary = "#FCB514" }
            "CGY", { Primary = "#C8102E"; Secondary = "#F1BE48" }
            "CAR", { Primary = "#CC0000"; Secondary = "#000000" }
            "CHI", { Primary = "#CF0A2C"; Secondary = "#000000" }
            "COL", { Primary = "#6F263D"; Secondary = "#236192" }
            "CBJ", { Primary = "#002654"; Secondary = "#CE1126" }
            "DAL", { Primary = "#006847"; Secondary = "#8F8F8C" }
            "DET", { Primary = "#CE1126"; Secondary = "#FFFFFF" }
            "EDM", { Primary = "#041E42"; Secondary = "#FF4C00" }
            "FLA", { Primary = "#041E42"; Secondary = "#C8102E" }
            "LA", { Primary = "#111111"; Secondary = "#A2AAAD" }
            "MIN", { Primary = "#154734"; Secondary = "#A6192E" }
            "MTL", { Primary = "#AF1E2D"; Secondary = "#192168" }
            "NSH", { Primary = "#FFB81C"; Secondary = "#041E42" }
            "NJ", { Primary = "#CE1126"; Secondary = "#000000" }
            "NYI", { Primary = "#00539B"; Secondary = "#F47D30" }
            "NYR", { Primary = "#0038A8"; Secondary = "#CE1126" }
            "OTT", { Primary = "#C52032"; Secondary = "#C2912C" }
            "PHI", { Primary = "#F74902"; Secondary = "#000000" }
            "PIT", { Primary = "#FCB514"; Secondary = "#000000" }
            "SJ", { Primary = "#006D75"; Secondary = "#EA7200" }
            "SEA", { Primary = "#99D9D9"; Secondary = "#001628" }
            "STL", { Primary = "#002F87"; Secondary = "#FCB514" }
            "TB", { Primary = "#002868"; Secondary = "#FFFFFF" }
            "TOR", { Primary = "#00205B"; Secondary = "#FFFFFF" }
            "VAN", { Primary = "#00205B"; Secondary = "#00843D" }
            "VGK", { Primary = "#B4975A"; Secondary = "#333F42" }
            "WAS", { Primary = "#C8102E"; Secondary = "#041E42" }
            "WSH", { Primary = "#C8102E"; Secondary = "#041E42" }
            "WPG", { Primary = "#041E42"; Secondary = "#004C97" }
            "UTA", { Primary = "#6CACE4"; Secondary = "#010101" }
        ]

    let private mlbColors =
        dict [
            "ARI", { Primary = "#A71930"; Secondary = "#E3D4AD" }
            "ATL", { Primary = "#CE1141"; Secondary = "#13274F" }
            "BAL", { Primary = "#DF4601"; Secondary = "#000000" }
            "BOS", { Primary = "#BD3039"; Secondary = "#0C2340" }
            "CHC", { Primary = "#0E3386"; Secondary = "#CC3433" }
            "CHW", { Primary = "#27251F"; Secondary = "#C4CED4" }
            "CWS", { Primary = "#27251F"; Secondary = "#C4CED4" }
            "CIN", { Primary = "#C6011F"; Secondary = "#000000" }
            "CLE", { Primary = "#00385D"; Secondary = "#E31937" }
            "COL", { Primary = "#33006F"; Secondary = "#C4CED4" }
            "DET", { Primary = "#0C2340"; Secondary = "#FA4616" }
            "HOU", { Primary = "#002D62"; Secondary = "#EB6E1F" }
            "KC", { Primary = "#004687"; Secondary = "#BD9B60" }
            "LAA", { Primary = "#BA0021"; Secondary = "#003263" }
            "LAD", { Primary = "#005A9C"; Secondary = "#EF3E42" }
            "MIA", { Primary = "#00A3E0"; Secondary = "#EF3340" }
            "MIL", { Primary = "#12284B"; Secondary = "#B6922E" }
            "MIN", { Primary = "#002B5C"; Secondary = "#D31145" }
            "NYM", { Primary = "#002D72"; Secondary = "#FF5910" }
            "NYY", { Primary = "#003087"; Secondary = "#C4CED4" }
            "ATH", { Primary = "#003831"; Secondary = "#EFB21E" }
            "OAK", { Primary = "#003831"; Secondary = "#EFB21E" }
            "PHI", { Primary = "#E81828"; Secondary = "#002D72" }
            "PIT", { Primary = "#27251F"; Secondary = "#FDB827" }
            "SD", { Primary = "#2F241D"; Secondary = "#FFC425" }
            "SF", { Primary = "#FD5A1E"; Secondary = "#27251F" }
            "SEA", { Primary = "#0C2C56"; Secondary = "#005C5C" }
            "STL", { Primary = "#C41E3A"; Secondary = "#0C2340" }
            "TB", { Primary = "#092C5C"; Secondary = "#8FBCE6" }
            "TEX", { Primary = "#003278"; Secondary = "#C0111F" }
            "TOR", { Primary = "#134A8E"; Secondary = "#1D2D5C" }
            "WAS", { Primary = "#AB0003"; Secondary = "#14225A" }
            "WSH", { Primary = "#AB0003"; Secondary = "#14225A" }
        ]

    let private f1Colors =
        dict [
            "Red Bull Racing", { Primary = "#3671C6"; Secondary = "#FFD700" }
            "Ferrari", { Primary = "#E8002D"; Secondary = "#FFEB3B" }
            "Mercedes", { Primary = "#27F4D2"; Secondary = "#000000" }
            "McLaren", { Primary = "#FF8000"; Secondary = "#47C7FC" }
            "Aston Martin", { Primary = "#229971"; Secondary = "#CEDC00" }
            "Alpine", { Primary = "#FF87BC"; Secondary = "#0093CC" }
            "Williams", { Primary = "#64C4FF"; Secondary = "#041E42" }
            "RB", { Primary = "#6692FF"; Secondary = "#1B3D6B" }
            "Kick Sauber", { Primary = "#52E252"; Secondary = "#000000" }
            "Haas", { Primary = "#B6BABD"; Secondary = "#E10600" }
        ]

    let private colorMaps =
        dict [
            Sport.NBA, nbaColors :> IDictionary<string, TeamColorPair>
            Sport.NFL, nflColors
            Sport.NHL, nhlColors
            Sport.MLB, mlbColors
            Sport.F1, f1Colors
            Sport.NASCAR, dict []
            Sport.INDYCAR, dict []
            Sport.WORLD_CUP, dict []
        ]

    let private hexToRgb (hex: string) =
        let hex = hex.TrimStart('#')
        if hex.Length = 6 then
            let r = Convert.ToInt32(hex.Substring(0, 2), 16)
            let g = Convert.ToInt32(hex.Substring(2, 2), 16)
            let b = Convert.ToInt32(hex.Substring(4, 2), 16)
            Some (r, g, b)
        else None

    let private srgbToLinear (c: int) =
        let s = float c / 255.0
        if s <= 0.03928 then s / 12.92 else ((s + 0.055) / 1.055) ** 2.4

    let private relativeLuminance (hex: string) =
        match hexToRgb hex with
        | Some (r, g, b) -> 0.2126 * srgbToLinear r + 0.7152 * srgbToLinear g + 0.0722 * srgbToLinear b
        | None -> 0.0

    let private bgLuminance = relativeLuminance "#111827"
    let private minContrastRatio = 2.0

    let private hasEnoughContrast (hex: string) =
        let lum = relativeLuminance hex
        let cr = (max lum bgLuminance + 0.05) / (min lum bgLuminance + 0.05)
        cr >= minContrastRatio

    let private lightenColor (hex: string) =
        match hexToRgb hex with
        | Some (r, g, b) ->
            let lighten c = min 255 (c + (255 - c) / 2)
            sprintf "#%02X%02X%02X" (lighten r) (lighten g) (lighten b)
        | None -> hex

    let private pickVisibleColor (colors: TeamColorPair) =
        if hasEnoughContrast colors.Primary then colors.Primary
        elif hasEnoughContrast colors.Secondary then colors.Secondary
        else lightenColor colors.Primary

    let getTeamColors (sport: Sport) (abbreviation: string) =
        match colorMaps.TryGetValue(sport) with
        | true, map ->
            let key = if sport = Sport.F1 then abbreviation else abbreviation.ToUpperInvariant()
            match map.TryGetValue(key) with
            | true, colors -> Some colors
            | _ -> None
        | _ -> None

    let getTeamColor (sport: Sport) (abbreviation: string) =
        getTeamColors sport abbreviation |> Option.map pickVisibleColor

    let getTeamColorOrDefault (sport: Sport) (abbreviation: string) (defaultColor: string) =
        getTeamColor sport abbreviation |> Option.defaultValue defaultColor

    type MatchupColors = { Home: string; Away: string }

    let private cardLuminance = relativeLuminance "#18181b"

    let private visibleOnCard color =
        (relativeLuminance color + 0.05) / (cardLuminance + 0.05) >= 3.0

    let private tint amount color =
        match hexToRgb color with
        | Some (r, g, b) ->
            let channel c = int (Math.Round(float c + float (255 - c) * amount))
            sprintf "#%02X%02X%02X" (channel r) (channel g) (channel b)
        | None -> "#808080"

    let private visibleTint color =
        [ 0.0 .. 0.05 .. 1.0 ]
        |> List.map (fun amount -> tint amount color)
        |> List.find visibleOnCard

    let private oklab color =
        let r, g, b = hexToRgb color |> Option.defaultValue (128, 128, 128)
        let r, g, b = srgbToLinear r, srgbToLinear g, srgbToLinear b
        let l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
        let m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
        let s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
        (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
         1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
         0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)

    let private separation first second =
        let l1, a1, b1 = oklab first
        let l2, a2, b2 = oklab second
        sqrt ((l1 - l2) ** 2.0 + (a1 - a2) ** 2.0 + (b1 - b2) ** 2.0)

    let private candidates palette =
        let primary = visibleTint palette.Primary
        [ primary, 0
          visibleTint palette.Secondary, 1
          tint 0.25 primary, 2
          tint 0.5 primary, 3
          "#808080", 10
          "#E5E7EB", 10 ]
        |> List.distinctBy fst

    let selectMatchupColors (home: TeamColorPair) (away: TeamColorPair) =
        [ for homeColor, homeCost in candidates home do
            for awayColor, awayCost in candidates away do
                if separation homeColor awayColor >= 0.12 then
                    yield homeCost + awayCost, { Home = homeColor; Away = awayColor } ]
        |> List.minBy fst
        |> snd

    let private resolveMatchup (sport: Nullable<Sport>) homeAbbr awayAbbr homeColor awayColor =
        let palette abbr providerColor =
            if sport.HasValue && sport.Value = Sport.CFB then
                let color = backend.Services.CollegeFootballPolicy.Color(providerColor)
                { Primary = color; Secondary = color }
            else
                let known =
                    if sport.HasValue && not (String.IsNullOrWhiteSpace abbr) then getTeamColors sport.Value abbr
                    else None
                known |> Option.defaultValue { Primary = "#6b7280"; Secondary = "#6b7280" }
        selectMatchupColors (palette homeAbbr homeColor) (palette awayAbbr awayColor)

    let forGame (game: backend.Models.Game) =
        resolveMatchup (SportExtensions.FromDbValue game.Sport)
            game.HomeTeamAbbreviation game.AwayTeamAbbreviation game.HomeColor game.AwayColor

    let forUpdate (update: GameUpdate) =
        resolveMatchup (Nullable update.Sport)
            update.HomeTeamAbbreviation update.AwayTeamAbbreviation update.HomeColor update.AwayColor
