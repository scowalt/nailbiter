namespace Backend.Rendering

open backend.GraphQL.Types

module LeagueConfig =

    type ClockFormat = Quarters | Periods | Innings | Halves

    type DisplayOptions =
        { ShowRanks: bool
          ShowPlayoffOdds: bool
          ShowBracket: bool
          ClockFormat: ClockFormat }

    type LeagueMeta =
        { Description: string
          Emoji: string }

    type LeagueColors =
        { Primary: string
          Secondary: string
          Gradient: string }

    type LeagueConfig =
        { Sport: Sport
          Name: string
          ShortName: string
          Path: string
          Colors: LeagueColors
          DefaultSort: string
          DisplayOptions: DisplayOptions
          Meta: LeagueMeta }

    let private configs =
        dict [
            Sport.NBA,
            { Sport = Sport.NBA
              Name = "NBA Basketball"
              ShortName = "NBA"
              Path = "/nba"
              Colors = { Primary = "#C9082A"; Secondary = "#17408B"; Gradient = "rgba(201, 8, 42, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = true; ShowBracket = true; ClockFormat = Quarters }
              Meta = { Description = "Live NBA games with win probability and excitement scores"; Emoji = "\U0001F3C0" } }

            Sport.NFL,
            { Sport = Sport.NFL
              Name = "NFL Football"
              ShortName = "NFL"
              Path = "/nfl"
              Colors = { Primary = "#013369"; Secondary = "#D50A0A"; Gradient = "rgba(1, 51, 105, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = true; ShowBracket = false; ClockFormat = Quarters }
              Meta = { Description = "Live NFL games with win probability and excitement scores"; Emoji = "\U0001F3C8" } }

            Sport.CFB,
            { Sport = Sport.CFB
              Name = "College Football"
              ShortName = "CFB"
              Path = "/cfb"
              Colors = { Primary = "#8B4513"; Secondary = "#C9A227"; Gradient = "rgba(139, 69, 19, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = true; ShowPlayoffOdds = true; ShowBracket = false; ClockFormat = Quarters }
              Meta = { Description = "FBS college football with FPI strength, CFP qualification and live excitement"; Emoji = "🏈" } }

            Sport.NHL,
            { Sport = Sport.NHL
              Name = "NHL Hockey"
              ShortName = "NHL"
              Path = "/nhl"
              Colors = { Primary = "#000000"; Secondary = "#A2AAAD"; Gradient = "rgba(0, 0, 0, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = true; ShowBracket = true; ClockFormat = Periods }
              Meta = { Description = "Live NHL games with win probability and excitement scores"; Emoji = "\U0001F3D2" } }

            Sport.MLB,
            { Sport = Sport.MLB
              Name = "MLB Baseball"
              ShortName = "MLB"
              Path = "/mlb"
              Colors = { Primary = "#002D72"; Secondary = "#D50032"; Gradient = "rgba(0, 45, 114, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = true; ShowBracket = false; ClockFormat = Innings }
              Meta = { Description = "Live MLB games with win probability and excitement scores"; Emoji = "⚾" } }

            Sport.F1,
            { Sport = Sport.F1
              Name = "Formula 1"
              ShortName = "F1"
              Path = "/f1"
              Colors = { Primary = "#E10600"; Secondary = "#15151E"; Gradient = "rgba(225, 6, 0, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = false; ShowBracket = false; ClockFormat = Quarters }
              Meta = { Description = "Live Formula 1 races with excitement scores"; Emoji = "\U0001F3CE️" } }

            Sport.NASCAR,
            { Sport = Sport.NASCAR
              Name = "NASCAR Cup Series"
              ShortName = "NASCAR"
              Path = "/nascar"
              Colors = { Primary = "#CF0A2C"; Secondary = "#003DA5"; Gradient = "rgba(207, 10, 44, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = false; ShowBracket = false; ClockFormat = Quarters }
              Meta = { Description = "Live NASCAR Cup Series races with excitement scores"; Emoji = "\U0001F3C1" } }

            Sport.INDYCAR,
            { Sport = Sport.INDYCAR
              Name = "IndyCar Series"
              ShortName = "IndyCar"
              Path = "/indycar"
              Colors = { Primary = "#CE1141"; Secondary = "#003DA5"; Gradient = "rgba(206, 17, 65, 0.08)" }
              DefaultSort = "excitement"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = false; ShowBracket = false; ClockFormat = Quarters }
              Meta = { Description = "Live IndyCar Series races with excitement scores"; Emoji = "\U0001F3C1" } }

            Sport.WORLD_CUP,
            { Sport = Sport.WORLD_CUP
              Name = "FIFA World Cup"
              ShortName = "World Cup"
              Path = "/world-cup"
              Colors = { Primary = "#7C3AED"; Secondary = "#10B981"; Gradient = "rgba(124, 58, 237, 0.08)" }
              DefaultSort = "kickoff"
              DisplayOptions = { ShowRanks = false; ShowPlayoffOdds = false; ShowBracket = false; ClockFormat = Halves }
              Meta = { Description = "World Cup matches with draw-aware probabilities and Nailbiter scores"; Emoji = "⚽" } }
        ]

    let getLeagueConfig (sport: Sport) = configs[sport]

    let getLeagueByPath (path: string) =
        configs.Values
        |> Seq.tryFind (fun c -> c.Path = path)

    let allLeagues =
        [ Sport.NBA; Sport.NFL; Sport.CFB; Sport.NHL; Sport.MLB; Sport.F1; Sport.NASCAR; Sport.INDYCAR ]
        |> List.map (fun s -> configs[s])
