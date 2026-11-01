
BEGIN;

ALTER TABLE odds_snapshots
    ADD COLUMN IF NOT EXISTS draw_moneyline INTEGER,
    ADD COLUMN IF NOT EXISTS draw_win_probability DOUBLE PRECISION;

ALTER TABLE pregame_odds
    ADD COLUMN IF NOT EXISTS draw_moneyline INTEGER,
    ADD COLUMN IF NOT EXISTS draw_win_probability DOUBLE PRECISION;

CREATE OR REPLACE VIEW latest_odds AS
SELECT DISTINCT ON (game_id)
    os.game_id,
    os.timestamp,
    os.home_win_probability,
    os.away_win_probability,
    os.home_moneyline,
    os.away_moneyline,
    os.spread,
    os.over_under,
    os.home_score,
    os.away_score,
    os.period,
    os.game_clock,
    g.sport,
    g.home_team,
    g.away_team,
    g.status,
    os.draw_win_probability,
    os.draw_moneyline
FROM odds_snapshots os
JOIN games g ON os.game_id = g.id
ORDER BY game_id, timestamp DESC;

CREATE OR REPLACE VIEW probability_changes AS
SELECT
    game_id,
    timestamp,
    home_win_probability,
    away_win_probability,
    home_win_probability - LAG(home_win_probability) OVER (
        PARTITION BY game_id ORDER BY timestamp
    ) AS home_prob_change,
    period,
    game_clock,
    draw_win_probability
FROM odds_snapshots;

COMMIT;
