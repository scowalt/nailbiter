namespace backend.Services

open System.Collections.Generic

type DivisionInfo =
    { Division: string
      Conference: string }

[<AbstractClass; Sealed>]
type TeamDivisionMappings =

    static member val NFL: Dictionary<string, DivisionInfo> =
        let d = Dictionary<string, DivisionInfo>()
        d.["BUF"] <- { Division = "AFC East"; Conference = "AFC" }
        d.["MIA"] <- { Division = "AFC East"; Conference = "AFC" }
        d.["NE"] <- { Division = "AFC East"; Conference = "AFC" }
        d.["NYJ"] <- { Division = "AFC East"; Conference = "AFC" }
        d.["BAL"] <- { Division = "AFC North"; Conference = "AFC" }
        d.["CIN"] <- { Division = "AFC North"; Conference = "AFC" }
        d.["CLE"] <- { Division = "AFC North"; Conference = "AFC" }
        d.["PIT"] <- { Division = "AFC North"; Conference = "AFC" }
        d.["HOU"] <- { Division = "AFC South"; Conference = "AFC" }
        d.["IND"] <- { Division = "AFC South"; Conference = "AFC" }
        d.["JAX"] <- { Division = "AFC South"; Conference = "AFC" }
        d.["TEN"] <- { Division = "AFC South"; Conference = "AFC" }
        d.["DEN"] <- { Division = "AFC West"; Conference = "AFC" }
        d.["KC"] <- { Division = "AFC West"; Conference = "AFC" }
        d.["LV"] <- { Division = "AFC West"; Conference = "AFC" }
        d.["LAC"] <- { Division = "AFC West"; Conference = "AFC" }
        d.["DAL"] <- { Division = "NFC East"; Conference = "NFC" }
        d.["NYG"] <- { Division = "NFC East"; Conference = "NFC" }
        d.["PHI"] <- { Division = "NFC East"; Conference = "NFC" }
        d.["WAS"] <- { Division = "NFC East"; Conference = "NFC" }
        d.["CHI"] <- { Division = "NFC North"; Conference = "NFC" }
        d.["DET"] <- { Division = "NFC North"; Conference = "NFC" }
        d.["GB"] <- { Division = "NFC North"; Conference = "NFC" }
        d.["MIN"] <- { Division = "NFC North"; Conference = "NFC" }
        d.["ATL"] <- { Division = "NFC South"; Conference = "NFC" }
        d.["CAR"] <- { Division = "NFC South"; Conference = "NFC" }
        d.["NO"] <- { Division = "NFC South"; Conference = "NFC" }
        d.["TB"] <- { Division = "NFC South"; Conference = "NFC" }
        d.["ARI"] <- { Division = "NFC West"; Conference = "NFC" }
        d.["LAR"] <- { Division = "NFC West"; Conference = "NFC" }
        d.["SF"] <- { Division = "NFC West"; Conference = "NFC" }
        d.["SEA"] <- { Division = "NFC West"; Conference = "NFC" }
        d

    static member val NBA: Dictionary<string, DivisionInfo> =
        let d = Dictionary<string, DivisionInfo>()
        d.["BOS"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["BKN"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["NY"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["NYK"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["PHI"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["TOR"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["CHI"] <- { Division = "Central"; Conference = "Eastern" }
        d.["CLE"] <- { Division = "Central"; Conference = "Eastern" }
        d.["DET"] <- { Division = "Central"; Conference = "Eastern" }
        d.["IND"] <- { Division = "Central"; Conference = "Eastern" }
        d.["MIL"] <- { Division = "Central"; Conference = "Eastern" }
        d.["ATL"] <- { Division = "Southeast"; Conference = "Eastern" }
        d.["CHA"] <- { Division = "Southeast"; Conference = "Eastern" }
        d.["MIA"] <- { Division = "Southeast"; Conference = "Eastern" }
        d.["ORL"] <- { Division = "Southeast"; Conference = "Eastern" }
        d.["WAS"] <- { Division = "Southeast"; Conference = "Eastern" }
        d.["WSH"] <- { Division = "Southeast"; Conference = "Eastern" }
        d.["DEN"] <- { Division = "Northwest"; Conference = "Western" }
        d.["MIN"] <- { Division = "Northwest"; Conference = "Western" }
        d.["OKC"] <- { Division = "Northwest"; Conference = "Western" }
        d.["POR"] <- { Division = "Northwest"; Conference = "Western" }
        d.["UTA"] <- { Division = "Northwest"; Conference = "Western" }
        d.["UTAH"] <- { Division = "Northwest"; Conference = "Western" }
        d.["GS"] <- { Division = "Pacific"; Conference = "Western" }
        d.["GSW"] <- { Division = "Pacific"; Conference = "Western" }
        d.["LAC"] <- { Division = "Pacific"; Conference = "Western" }
        d.["LAL"] <- { Division = "Pacific"; Conference = "Western" }
        d.["PHX"] <- { Division = "Pacific"; Conference = "Western" }
        d.["SAC"] <- { Division = "Pacific"; Conference = "Western" }
        d.["DAL"] <- { Division = "Southwest"; Conference = "Western" }
        d.["HOU"] <- { Division = "Southwest"; Conference = "Western" }
        d.["MEM"] <- { Division = "Southwest"; Conference = "Western" }
        d.["NO"] <- { Division = "Southwest"; Conference = "Western" }
        d.["NOP"] <- { Division = "Southwest"; Conference = "Western" }
        d.["SA"] <- { Division = "Southwest"; Conference = "Western" }
        d.["SAS"] <- { Division = "Southwest"; Conference = "Western" }
        d

    static member val NHL: Dictionary<string, DivisionInfo> =
        let d = Dictionary<string, DivisionInfo>()
        d.["BOS"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["BUF"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["DET"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["FLA"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["MTL"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["OTT"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["TB"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["TOR"] <- { Division = "Atlantic"; Conference = "Eastern" }
        d.["CAR"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["CBJ"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["NJ"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["NYI"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["NYR"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["PHI"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["PIT"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["WAS"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["WSH"] <- { Division = "Metropolitan"; Conference = "Eastern" }
        d.["ARI"] <- { Division = "Central"; Conference = "Western" }
        d.["UTA"] <- { Division = "Central"; Conference = "Western" }
        d.["CHI"] <- { Division = "Central"; Conference = "Western" }
        d.["COL"] <- { Division = "Central"; Conference = "Western" }
        d.["DAL"] <- { Division = "Central"; Conference = "Western" }
        d.["MIN"] <- { Division = "Central"; Conference = "Western" }
        d.["NSH"] <- { Division = "Central"; Conference = "Western" }
        d.["STL"] <- { Division = "Central"; Conference = "Western" }
        d.["WPG"] <- { Division = "Central"; Conference = "Western" }
        d.["ANA"] <- { Division = "Pacific"; Conference = "Western" }
        d.["CGY"] <- { Division = "Pacific"; Conference = "Western" }
        d.["EDM"] <- { Division = "Pacific"; Conference = "Western" }
        d.["LA"] <- { Division = "Pacific"; Conference = "Western" }
        d.["SJ"] <- { Division = "Pacific"; Conference = "Western" }
        d.["SEA"] <- { Division = "Pacific"; Conference = "Western" }
        d.["VAN"] <- { Division = "Pacific"; Conference = "Western" }
        d.["VGK"] <- { Division = "Pacific"; Conference = "Western" }
        d

    static member val MLB: Dictionary<string, DivisionInfo> =
        let d = Dictionary<string, DivisionInfo>()
        d.["BAL"] <- { Division = "AL East"; Conference = "AL" }
        d.["BOS"] <- { Division = "AL East"; Conference = "AL" }
        d.["NYY"] <- { Division = "AL East"; Conference = "AL" }
        d.["TB"] <- { Division = "AL East"; Conference = "AL" }
        d.["TOR"] <- { Division = "AL East"; Conference = "AL" }
        d.["CHW"] <- { Division = "AL Central"; Conference = "AL" }
        d.["CWS"] <- { Division = "AL Central"; Conference = "AL" }
        d.["CLE"] <- { Division = "AL Central"; Conference = "AL" }
        d.["DET"] <- { Division = "AL Central"; Conference = "AL" }
        d.["KC"] <- { Division = "AL Central"; Conference = "AL" }
        d.["MIN"] <- { Division = "AL Central"; Conference = "AL" }
        d.["HOU"] <- { Division = "AL West"; Conference = "AL" }
        d.["LAA"] <- { Division = "AL West"; Conference = "AL" }
        d.["ATH"] <- { Division = "AL West"; Conference = "AL" }
        d.["SEA"] <- { Division = "AL West"; Conference = "AL" }
        d.["TEX"] <- { Division = "AL West"; Conference = "AL" }
        d.["ATL"] <- { Division = "NL East"; Conference = "NL" }
        d.["MIA"] <- { Division = "NL East"; Conference = "NL" }
        d.["NYM"] <- { Division = "NL East"; Conference = "NL" }
        d.["PHI"] <- { Division = "NL East"; Conference = "NL" }
        d.["WAS"] <- { Division = "NL East"; Conference = "NL" }
        d.["WSH"] <- { Division = "NL East"; Conference = "NL" }
        d.["CHC"] <- { Division = "NL Central"; Conference = "NL" }
        d.["CIN"] <- { Division = "NL Central"; Conference = "NL" }
        d.["MIL"] <- { Division = "NL Central"; Conference = "NL" }
        d.["PIT"] <- { Division = "NL Central"; Conference = "NL" }
        d.["STL"] <- { Division = "NL Central"; Conference = "NL" }
        d.["ARI"] <- { Division = "NL West"; Conference = "NL" }
        d.["COL"] <- { Division = "NL West"; Conference = "NL" }
        d.["LAD"] <- { Division = "NL West"; Conference = "NL" }
        d.["SD"] <- { Division = "NL West"; Conference = "NL" }
        d.["SF"] <- { Division = "NL West"; Conference = "NL" }
        d

    static member GetDivisionInfo(sport: string, teamAbbreviation: string) =
        let sportUpper = sport.ToUpperInvariant()
        let teamUpper = teamAbbreviation.ToUpperInvariant()

        let dict =
            match sportUpper with
            | "NFL" -> Some TeamDivisionMappings.NFL
            | "NBA" -> Some TeamDivisionMappings.NBA
            | "NHL" -> Some TeamDivisionMappings.NHL
            | "MLB" -> Some TeamDivisionMappings.MLB
            | _ -> None

        match dict with
        | None -> Unchecked.defaultof<DivisionInfo>
        | Some d ->
            match d.TryGetValue(teamUpper) with
            | true, info -> info
            | false, _ -> Unchecked.defaultof<DivisionInfo>
