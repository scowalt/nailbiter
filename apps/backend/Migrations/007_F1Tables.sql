CREATE TABLE IF NOT EXISTS f1_races (
    id TEXT PRIMARY KEY,   
    meeting_name TEXT NOT NULL,
    circuit_name TEXT NOT NULL,
    circuit_short_name TEXT NOT NULL,
    country TEXT NOT NULL,
    year INTEGER NOT NULL,
    round_number INTEGER NOT NULL,
    scheduled_start TIMESTAMPTZ NOT NULL,
    actual_start TIMESTAMPTZ,
    end_time TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'scheduled',
    total_laps INTEGER NOT NULL DEFAULT 0,
    completed_laps INTEGER
);

CREATE INDEX IF NOT EXISTS idx_f1_races_year ON f1_races (year);
CREATE INDEX IF NOT EXISTS idx_f1_races_status ON f1_races (status);
CREATE INDEX IF NOT EXISTS idx_f1_races_scheduled_start ON f1_races (scheduled_start);
CREATE UNIQUE INDEX IF NOT EXISTS idx_f1_races_year_round ON f1_races (year, round_number);

CREATE TABLE IF NOT EXISTS f1_drivers (
    id SERIAL PRIMARY KEY,
    driver_number TEXT NOT NULL,
    full_name TEXT NOT NULL,
    abbreviation TEXT NOT NULL,
    team_name TEXT NOT NULL,
    team_color TEXT NOT NULL,
    headshot_url TEXT,
    country_code TEXT,
    season INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_f1_drivers_season ON f1_drivers (season);
CREATE UNIQUE INDEX IF NOT EXISTS idx_f1_drivers_number_season ON f1_drivers (driver_number, season);

CREATE TABLE IF NOT EXISTS f1_championship_standings (
    id SERIAL PRIMARY KEY,
    season INTEGER NOT NULL,
    type TEXT NOT NULL,   
    entity_name TEXT NOT NULL,
    abbreviation TEXT NOT NULL,
    position INTEGER NOT NULL,
    points DOUBLE PRECISION NOT NULL DEFAULT 0,
    wins INTEGER NOT NULL DEFAULT 0,
    after_round INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_f1_standings_season_type ON f1_championship_standings (season, type);
CREATE INDEX IF NOT EXISTS idx_f1_standings_season_type_round ON f1_championship_standings (season, type, after_round);
