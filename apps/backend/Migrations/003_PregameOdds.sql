
CREATE TABLE IF NOT EXISTS pregame_odds (
    game_id VARCHAR(50) PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
    home_moneyline INTEGER NOT NULL,
    away_moneyline INTEGER NOT NULL,
    spread DOUBLE PRECISION,
    over_under DOUBLE PRECISION,
    home_win_probability DOUBLE PRECISION NOT NULL,
    away_win_probability DOUBLE PRECISION NOT NULL,
    fetched_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    source VARCHAR(50) DEFAULT 'yahoo'
);

CREATE INDEX IF NOT EXISTS idx_pregame_odds_fetched_at ON pregame_odds(fetched_at);
