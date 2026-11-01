CREATE TABLE IF NOT EXISTS nascar_races (
    id TEXT PRIMARY KEY,   
    race_name TEXT NOT NULL,
    year INTEGER NOT NULL,
    race_number INTEGER NOT NULL,
    scheduled_start TIMESTAMPTZ NOT NULL,
    actual_start TIMESTAMPTZ,
    end_time TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'scheduled',
    total_laps INTEGER NOT NULL DEFAULT 0,
    completed_laps INTEGER
);

CREATE INDEX IF NOT EXISTS idx_nascar_races_year ON nascar_races (year);
CREATE INDEX IF NOT EXISTS idx_nascar_races_status ON nascar_races (status);
CREATE INDEX IF NOT EXISTS idx_nascar_races_scheduled_start ON nascar_races (scheduled_start);
CREATE UNIQUE INDEX IF NOT EXISTS idx_nascar_races_year_race_number ON nascar_races (year, race_number);

CREATE TABLE IF NOT EXISTS nascar_drivers (
    id SERIAL PRIMARY KEY,
    espn_athlete_id TEXT NOT NULL,
    full_name TEXT NOT NULL,
    display_name TEXT NOT NULL,
    short_name TEXT NOT NULL,
    country_flag_href TEXT,
    season INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_nascar_drivers_season ON nascar_drivers (season);
CREATE UNIQUE INDEX IF NOT EXISTS idx_nascar_drivers_athlete_season ON nascar_drivers (espn_athlete_id, season);
