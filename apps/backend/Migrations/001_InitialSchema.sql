
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS games (
    id VARCHAR(50) PRIMARY KEY,
    sport VARCHAR(20) NOT NULL,
    home_team VARCHAR(100) NOT NULL,
    away_team VARCHAR(100) NOT NULL,
    home_team_abbr VARCHAR(10) NOT NULL,
    away_team_abbr VARCHAR(10) NOT NULL,
    scheduled_start TIMESTAMPTZ NOT NULL,
    actual_start TIMESTAMPTZ,
    end_time TIMESTAMPTZ,
    status VARCHAR(20) NOT NULL DEFAULT 'scheduled',
    home_score INTEGER,
    away_score INTEGER
);

CREATE INDEX IF NOT EXISTS idx_games_sport ON games(sport);
CREATE INDEX IF NOT EXISTS idx_games_status ON games(status);
CREATE INDEX IF NOT EXISTS idx_games_scheduled_start ON games(scheduled_start);

CREATE TABLE IF NOT EXISTS odds_snapshots (
    id BIGSERIAL,
    game_id VARCHAR(50) NOT NULL REFERENCES games(id),
    timestamp TIMESTAMPTZ NOT NULL,
    home_moneyline INTEGER NOT NULL,
    away_moneyline INTEGER NOT NULL,
    spread DOUBLE PRECISION,
    over_under DOUBLE PRECISION,
    home_win_probability DOUBLE PRECISION NOT NULL,
    away_win_probability DOUBLE PRECISION NOT NULL,
    game_clock VARCHAR(20),
    period INTEGER,
    home_score INTEGER,
    away_score INTEGER,
    PRIMARY KEY (id, timestamp)
);

SELECT create_hypertable(
    'odds_snapshots',
    'timestamp',
    chunk_time_interval => INTERVAL '1 day',
    if_not_exists => TRUE
);

CREATE INDEX IF NOT EXISTS idx_odds_game_time ON odds_snapshots(game_id, timestamp DESC);

ALTER TABLE odds_snapshots SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'game_id'
);

SELECT add_compression_policy('odds_snapshots', INTERVAL '7 days', if_not_exists => TRUE);


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
    g.status
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
    game_clock
FROM odds_snapshots;
