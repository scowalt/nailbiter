namespace Backend.Rendering

open System.Collections.Generic
open backend.GraphQL.Types

module Logos =

    let private espnTeamLogoBase = "https://a.espncdn.com/i/teamlogos"

    let leagueLogos =
        dict [
            Sport.NBA, "/logos/nba.png"
            Sport.NFL, "/logos/nfl.png"
            Sport.CFB, "/logos/cfb.svg"
            Sport.NHL, "/logos/nhl.png"
            Sport.MLB, "/logos/mlb.png"
            Sport.F1, "/logos/f1.png"
            Sport.NASCAR, "/logos/nascar.png"
            Sport.INDYCAR, "/logos/indycar.png"
            Sport.WORLD_CUP, "/logos/world-cup.svg"
        ]

    let private nbaTeams =
        dict [
            "ATL", "atl"; "BOS", "bos"; "BKN", "bkn"; "CHA", "cha"; "CHI", "chi"
            "CLE", "cle"; "DAL", "dal"; "DEN", "den"; "DET", "det"; "GS", "gs"
            "GSW", "gs"; "HOU", "hou"; "IND", "ind"; "LAC", "lac"; "LAL", "lal"
            "MEM", "mem"; "MIA", "mia"; "MIL", "mil"; "MIN", "min"; "NO", "no"
            "NOP", "no"; "NY", "ny"; "NYK", "ny"; "OKC", "okc"; "ORL", "orl"
            "PHI", "phi"; "PHX", "phx"; "POR", "por"; "SA", "sa"; "SAC", "sac"
            "SAS", "sa"; "TOR", "tor"; "UTA", "utah"; "UTAH", "utah"; "WAS", "wsh"
            "WSH", "wsh"
        ]

    let private nflTeams =
        dict [
            "ARI", "ari"; "ATL", "atl"; "BAL", "bal"; "BUF", "buf"; "CAR", "car"
            "CHI", "chi"; "CIN", "cin"; "CLE", "cle"; "DAL", "dal"; "DEN", "den"
            "DET", "det"; "GB", "gb"; "HOU", "hou"; "IND", "ind"; "JAX", "jax"
            "KC", "kc"; "LV", "lv"; "LAC", "lac"; "LAR", "lar"; "MIA", "mia"
            "MIN", "min"; "NE", "ne"; "NO", "no"; "NYG", "nyg"; "NYJ", "nyj"
            "PHI", "phi"; "PIT", "pit"; "SF", "sf"; "SEA", "sea"; "TB", "tb"
            "TEN", "ten"; "WAS", "wsh"; "WSH", "wsh"
        ]

    let private nhlTeams =
        dict [
            "ANA", "ana"; "ARI", "ari"; "BOS", "bos"; "BUF", "buf"; "CGY", "cgy"
            "CAR", "car"; "CHI", "chi"; "COL", "col"; "CBJ", "cbj"; "DAL", "dal"
            "DET", "det"; "EDM", "edm"; "FLA", "fla"; "LA", "la"; "MIN", "min"
            "MTL", "mtl"; "NSH", "nsh"; "NJ", "nj"; "NYI", "nyi"; "NYR", "nyr"
            "OTT", "ott"; "PHI", "phi"; "PIT", "pit"; "SJ", "sj"; "SEA", "sea"
            "STL", "stl"; "TB", "tb"; "TOR", "tor"; "VAN", "van"; "VGK", "vgk"
            "WAS", "wsh"; "WSH", "wsh"; "WPG", "wpg"; "UTA", "uta"
        ]

    let private mlbTeams =
        dict [
            "ARI", "ari"; "ATL", "atl"; "BAL", "bal"; "BOS", "bos"; "CHC", "chc"
            "CHW", "chw"; "CWS", "chw"; "CIN", "cin"; "CLE", "cle"; "COL", "col"
            "DET", "det"; "HOU", "hou"; "KC", "kc"; "LAA", "laa"; "LAD", "lad"
            "MIA", "mia"; "MIL", "mil"; "MIN", "min"; "NYM", "nym"; "NYY", "nyy"
            "ATH", "ath"; "OAK", "oak"; "PHI", "phi"; "PIT", "pit"; "SD", "sd"
            "SF", "sf"; "SEA", "sea"; "STL", "stl"; "TB", "tb"; "TEX", "tex"
            "TOR", "tor"; "WAS", "wsh"; "WSH", "wsh"
        ]

    let private teamMaps =
        dict [
            Sport.NBA, nbaTeams :> IDictionary<string, string>
            Sport.NFL, nflTeams
            Sport.NHL, nhlTeams
            Sport.MLB, mlbTeams
            Sport.F1, dict []
            Sport.NASCAR, dict []
            Sport.INDYCAR, dict []
            Sport.WORLD_CUP, dict []
        ]

    let private sportPaths =
        dict [
            Sport.NBA, "nba"
            Sport.NFL, "nfl"
            Sport.NHL, "nhl"
            Sport.MLB, "mlb"
            Sport.F1, "f1"
            Sport.NASCAR, "nascar"
            Sport.INDYCAR, "indycar"
            Sport.WORLD_CUP, "soccer"
        ]

    let getTeamLogoUrl (sport: Sport) (abbreviation: string) =
        match teamMaps.TryGetValue(sport) with
        | true, map ->
            match map.TryGetValue(abbreviation.ToUpperInvariant()) with
            | true, slug ->
                let sportPath = sportPaths[sport]
                Some $"{espnTeamLogoBase}/{sportPath}/500/scoreboard/{slug}.png"
            | _ -> None
        | _ -> None

    let getLeagueLogoUrl (sport: Sport) =
        match leagueLogos.TryGetValue(sport) with
        | true, url -> url
        | _ -> ""
