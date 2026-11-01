ALTER TABLE nascar_drivers ADD COLUMN IF NOT EXISTS car_number TEXT;
ALTER TABLE nascar_drivers ADD COLUMN IF NOT EXISTS manufacturer TEXT;
ALTER TABLE nascar_drivers ADD COLUMN IF NOT EXISTS team_name TEXT;

ALTER TABLE nascar_race_results ADD COLUMN IF NOT EXISTS car_number TEXT;

ALTER TABLE nascar_races ADD COLUMN IF NOT EXISTS excitement_score DOUBLE PRECISION;

CREATE TABLE IF NOT EXISTS nascar_race_snapshots (
    id BIGSERIAL PRIMARY KEY,
    race_id TEXT NOT NULL REFERENCES nascar_races(id) ON DELETE CASCADE,
    espn_athlete_id TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL,
    position INTEGER,
    win_probability DOUBLE PRECISION,
    completed_laps INTEGER,
    source TEXT
);
CREATE INDEX IF NOT EXISTS idx_nascar_snapshots_race_time ON nascar_race_snapshots (race_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_nascar_snapshots_race_athlete ON nascar_race_snapshots (race_id, espn_athlete_id, timestamp);
