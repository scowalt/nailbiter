ALTER TABLE games
    ADD COLUMN IF NOT EXISTS home_team_id text,
    ADD COLUMN IF NOT EXISTS away_team_id text,
    ADD COLUMN IF NOT EXISTS home_color text,
    ADD COLUMN IF NOT EXISTS away_color text,
    ADD COLUMN IF NOT EXISTS period integer,
    ADD COLUMN IF NOT EXISTS game_clock text,
    ADD COLUMN IF NOT EXISTS status_detail text,
    ADD COLUMN IF NOT EXISTS scoreboard_updated_at timestamptz,
    ADD COLUMN IF NOT EXISTS home_market_ticker text,
    ADD COLUMN IF NOT EXISTS away_market_ticker text,
    ADD COLUMN IF NOT EXISTS college_competition text,
    ADD COLUMN IF NOT EXISTS competition_evidence text,
    ADD COLUMN IF NOT EXISTS is_historical_import boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS home_fpi double precision,
    ADD COLUMN IF NOT EXISTS away_fpi double precision,
    ADD COLUMN IF NOT EXISTS home_ap_rank integer,
    ADD COLUMN IF NOT EXISTS away_ap_rank integer,
    ADD COLUMN IF NOT EXISTS ratings_observed_at timestamptz,
    ADD COLUMN IF NOT EXISTS market_observed_at timestamptz;
ALTER TABLE odds_snapshots ADD COLUMN IF NOT EXISTS is_historical boolean NOT NULL DEFAULT false;
ALTER TABLE pregame_odds ALTER COLUMN source TYPE text;
CREATE TABLE IF NOT EXISTS college_teams (
    season integer NOT NULL, espn_id text NOT NULL, abbreviation text NOT NULL, name text NOT NULL,
    fpi double precision, fpi_updated_at timestamptz NOT NULL, ap_rank integer,
    ap_updated_at timestamptz, fetched_at timestamptz NOT NULL,
    PRIMARY KEY (season, espn_id)
);
CREATE TABLE IF NOT EXISTS college_imports (
    id text PRIMARY KEY CHECK (id = 'initial'), season integer NOT NULL,
    first_import_at timestamptz NOT NULL, window_start timestamptz NOT NULL,
    CHECK (window_start >= first_import_at - interval '14 days')
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_college_history_observation
    ON odds_snapshots (game_id, timestamp, source) WHERE is_historical;
