namespace backend.Services

open System
open System.Globalization

module QualificationQuotePolicy =
    let decimalValue (text: string) =
        match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> Some value
        | _ -> None

    let evaluate bid ask bidSize askSize =
        match bid, ask, bidSize, askSize with
        | Some bid, Some ask, Some bidSize, Some askSize ->
            if bid < 0M || ask > 1M || bid > ask || bidSize < 0M || askSize < 0M then Error "malformed_quote"
            elif ask - bid > 0.25M then Error "wide_spread"
            else
                let twoSided = bidSize > 0M && askSize > 0M
                let highBound = bid >= 0.99M && bidSize > 0M && ask = 1M && askSize = 0M
                let lowBound = ask <= 0.01M && askSize > 0M && bid = 0M && bidSize = 0M
                if twoSided || highBound || lowBound then Ok ((bid + ask) / 2M)
                else Error "insufficient_depth"
        | _ -> Error "malformed_quote"
