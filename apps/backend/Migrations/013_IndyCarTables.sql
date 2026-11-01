CREATE TABLE IF NOT EXISTS indycar_races (
    id TEXT PRIMARY KEY,   
    race_name TEXT NOT NULL,
    year INTEGER NOT NULL,
    race_number INTEGER NOT NULL,
    scheduled_start TIMESTAMPTZ NOT NULL,
    actual_start TIMESTAMPTZ,
    end_time TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'scheduled',
    total_laps INTEGER NOT NULL DEFAULT 0,
    completed_laps INTEGER,
    excitement_score DOUBLE PRECISION
);

CREATE INDEX IF NOT EXISTS idx_indycar_races_year ON indycar_races (year);
CREATE INDEX IF NOT EXISTS idx_indycar_races_status ON indycar_races (status);
CREATE INDEX IF NOT EXISTS idx_indycar_races_scheduled_start ON indycar_races (scheduled_start);
CREATE UNIQUE INDEX IF NOT EXISTS idx_indycar_races_year_race_number ON indycar_races (year, race_number);

CREATE TABLE IF NOT EXISTS indycar_drivers (
    id SERIAL PRIMARY KEY,
    espn_athlete_id TEXT NOT NULL,
    full_name TEXT NOT NULL,
    display_name TEXT NOT NULL,
    short_name TEXT NOT NULL,
    country_flag_href TEXT,
    car_number TEXT,
    team_name TEXT,
    season INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_indycar_drivers_season ON indycar_drivers (season);
CREATE UNIQUE INDEX IF NOT EXISTS idx_indycar_drivers_athlete_season ON indycar_drivers (espn_athlete_id, season);

CREATE TABLE IF NOT EXISTS indycar_race_results (
    id SERIAL PRIMARY KEY,
    race_id TEXT NOT NULL REFERENCES indycar_races(id),
    espn_athlete_id TEXT NOT NULL,
    display_name TEXT NOT NULL,
    short_name TEXT NOT NULL,
    finish_position INTEGER,
    winner BOOLEAN NOT NULL DEFAULT FALSE,
    country_flag_href TEXT,
    car_number TEXT
);

CREATE INDEX IF NOT EXISTS idx_indycar_race_results_race_id ON indycar_race_results (race_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_indycar_race_results_race_athlete ON indycar_race_results (race_id, espn_athlete_id);
