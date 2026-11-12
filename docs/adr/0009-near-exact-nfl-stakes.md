# Score NFL Stakes bounds within one point

On 2026-11-12 MIA @ IND showed "Hype unavailable" with valid PlayoffStatus forecasts for both teams. Both teams were near elimination, so several cells read `<1%`. Those thresholds stay intervals, and the game's Stakes bounds were 0 and 0.8. Stakes was numeric only when the bounds were exactly equal, so one input that moves hype by at most 0.36 points blanked the whole score.

The owner approved scoring game Stakes when its upper and lower bounds are at most one point apart. The score is the midpoint of the bounds. Wider bounds stay unavailable, and threshold cells stay intervals in the forecast.

Equal bounds score the same value as before, so existing NFL snapshots stay valid and `FormulaVersion` does not change. NBA, NHL and MLB Stakes always have equal bounds and are unaffected.
