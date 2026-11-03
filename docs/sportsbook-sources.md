# Sportsbook and provider boundaries

## Current policy

For NFL/NBA/NHL/MLB expected closeness, prefer eligible Kalshi game-winner quotes, with the existing whole-pair ESPN fallback. ESPN discovers/verifies games and schedules. Both outcome contracts must match one unambiguous game; preserve executable-price/depth, freshness, game applicability and alias checks. Missing or unusable evidence remains unavailable. See [live odds](live-odds.md) and [pro-hype policy](pro-hype-evidence.md).

Playoff qualification is a different question from game-winner odds or championship futures. Use eligible make/miss Kalshi evidence and verified final outcomes. The narrow owner-approved MoneyPuck NHL model exception is independently controlled, credited and nonfinal. It does not authorize other model sources or mix markets and forecasts. [ADR 0002](adr/0002-nhl-market-only-qualification.md) and [ADR 0003](adr/0003-nhl-moneypuck-qualification-exception.md) record the distinction.

## Preserved restrictions

- All MoneyPuck collection shares at least 24 hours between attempts, including failed/cancelled attempts, across callers/restarts. A refusal blocks both inputs for at least 24 hours from observation (longer for a valid later Retry-After); the owner-approved TTL survives restart. Only a normal daily attempt may resume after both deadlines, with a new refusal renewing backoff. Active/unfinalized cycles remain fail-closed. Qualification application defaults remain off; production Compose follows the owner's explicit collection/selection settings. Enabling selection does not establish live-source readiness or bypass missing/stale evidence, unexpired refusal backoff or the daily budget.
- Polymarket is rejected for Nailbiter; do not resume investigation, denied-source probing or importer/rollout work without a new explicit decision.
- Legacy PlayoffStatus remains rejected/default off, not an awaiting-permission production path.
- Free-only remains a constraint. Do not add paid/trial feeds, enroll accounts, contact providers for extra permission, or use unsupported proxy/identity/access workarounds without a separately authorized scope.
- Existing access approval is not a blanket public data-redistribution license. Test fixture generation is offline and independently synthetic; it does not authorize collection.

Historical source research is private evidence, not a public-build dependency. New independent provider checks require bounded authorization and must stop on denial. Passing the public fixture suite is not proof of recurring access, source semantics or sustained coverage.
