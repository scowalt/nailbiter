namespace backend.Services

open System

module ExcitementScore =
    let private multiplier = 20.0
    let private reversalThreshold = 0.03

    let private thresholdComparisonTolerance = 1e-12
    let private meaningful movement = movement >= reversalThreshold - thresholdComparisonTolerance

    let private suspenseIntegral p =
        if p <= 0.5 then 4.0 * p * p * p / 3.0
        else
            let away = 1.0 - p
            1.0 / 3.0 - 4.0 * away * away * away / 3.0

    let private calculateSuspenseWeighted (probabilities: float seq) : Nullable<float> =
        let points =
            probabilities
            |> Seq.filter (fun p -> Double.IsFinite p && p >= 0.0 && p <= 1.0)
            |> Seq.toArray
        if points.Length < 2 then Nullable<float>()
        else
            let mutable direction = 0
            let mutable initialLow = points[0]
            let mutable initialHigh = points[0]
            let mutable extreme = points[0]
            let mutable credit = 0.0
            for p in points |> Seq.skip 1 do
                if direction = 0 then
                    initialLow <- min initialLow p
                    initialHigh <- max initialHigh p
                    if meaningful (initialHigh - p) then
                        direction <- -1
                        credit <- abs (suspenseIntegral p - suspenseIntegral initialHigh)
                        extreme <- p
                    elif meaningful (p - initialLow) then
                        direction <- 1
                        credit <- abs (suspenseIntegral p - suspenseIntegral initialLow)
                        extreme <- p
                else
                    let movement = p - extreme
                    let grows = (direction > 0 && movement > 0.0) || (direction < 0 && movement < 0.0)
                    if grows || meaningful (abs movement) then
                        direction <- if movement > 0.0 then 1 else -1
                        credit <- credit + abs (suspenseIntegral p - suspenseIntegral extreme)
                        extreme <- p
            Nullable(min 100.0 (multiplier * credit))

    let private calculateRawMovement (probabilities: float seq) : Nullable<float> =
        let points = probabilities |> Seq.toArray
        if points.Length < 2 then Nullable<float>()
        else
            let mutable totalChange = 0.0
            for i in 1 .. points.Length - 1 do
                totalChange <- totalChange + abs (points[i] - points[i - 1])
            Nullable(Math.Min(100.0, totalChange * 20.0))

    let Calculate (sport: string) (probabilities: float seq) : Nullable<float> =
        match sport with
        | "cfb" | "nfl" | "nba" | "nhl" | "mlb" -> calculateSuspenseWeighted probabilities
        | _ -> calculateRawMovement probabilities
