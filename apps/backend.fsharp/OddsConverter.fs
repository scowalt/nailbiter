namespace backend.Services

open System

[<AbstractClass; Sealed>]
type OddsConverter =

    static member MoneylineToImpliedProbability(moneyline: int) : float =
        if moneyline < 0 then
            float (abs moneyline) / (float (abs moneyline) + 100.0)
        else
            100.0 / (float moneyline + 100.0)

    static member ToNormalizedProbabilities(homeMoneyline: int, awayMoneyline: int) : struct (float * float) =
        let homeImplied = OddsConverter.MoneylineToImpliedProbability(homeMoneyline)
        let awayImplied = OddsConverter.MoneylineToImpliedProbability(awayMoneyline)
        let total = homeImplied + awayImplied
        struct (homeImplied / total, awayImplied / total)

    static member ToNormalizedThreeWayProbabilities(homeMoneyline: int, drawMoneyline: int, awayMoneyline: int) : struct (float * float * float) =
        let homeImplied = OddsConverter.MoneylineToImpliedProbability(homeMoneyline)
        let drawImplied = OddsConverter.MoneylineToImpliedProbability(drawMoneyline)
        let awayImplied = OddsConverter.MoneylineToImpliedProbability(awayMoneyline)
        let total = homeImplied + drawImplied + awayImplied
        struct (homeImplied / total, drawImplied / total, awayImplied / total)

    static member TryParseMoneyline(odds: string) : int option =
        if String.IsNullOrWhiteSpace(odds) then
            None
        elif odds.Equals("EVEN", StringComparison.OrdinalIgnoreCase) then
            Some 100
        else
            let mutable parsed = 0
            match Int32.TryParse(odds.Replace("+", ""), &parsed) with
            | true -> Some parsed
            | false -> None

    static member ParseMoneyline(odds: string) : int =
        OddsConverter.TryParseMoneyline(odds) |> Option.defaultValue 0
