# Use Kalshi game-winner evidence for missing college pregame moneylines

On 2026-10-10 the user approved ESPN moneylines → Kalshi two-sided prices → depth-backed near-certain bounds → unavailable as the college pregame closeness policy. Extreme college mismatches can lack sportsbook moneylines despite usable evidence from our existing Kalshi provider; this avoids adding a provider or silently substituting a model, spread-derived price, or neutral default.

Store probability-only observations with null moneylines and explicit provenance, and expire both Kalshi fallbacks after two minutes. Bounds use the existing scoring representatives while displaying bounds, not exact probabilities; settled outcomes cannot reconstruct pregame expectations. Nullable stored prices require a repeatable migration and a data review before rolling back to an older non-null-reading binary.
