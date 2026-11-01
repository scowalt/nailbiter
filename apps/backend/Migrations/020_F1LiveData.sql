ALTER TABLE f1_races ADD COLUMN IF NOT EXISTS excitement_score DOUBLE PRECISION;

CREATE TABLE IF NOT EXISTS f1_race_snapshots (
    id BIGSERIAL PRIMARY KEY,
    race_id TEXT NOT NULL REFERENCES f1_races(id) ON DELETE CASCADE,
    espn_athlete_id TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL,
    position INTEGER,
    win_probability DOUBLE PRECISION,
    completed_laps INTEGER,
    source TEXT
);
CREATE INDEX IF NOT EXISTS idx_f1_snapshots_race_time ON f1_race_snapshots (race_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_f1_snapshots_race_athlete ON f1_race_snapshots (race_id, espn_athlete_id, timestamp);
