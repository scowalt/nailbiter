CREATE TABLE IF NOT EXISTS indycar_race_snapshots (
    id BIGSERIAL PRIMARY KEY,
    race_id TEXT NOT NULL REFERENCES indycar_races(id),
    espn_athlete_id TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL,
    position INTEGER,
    win_probability DOUBLE PRECISION,
    completed_laps INTEGER,
    source TEXT
);

CREATE INDEX IF NOT EXISTS idx_indycar_snapshots_race_time ON indycar_race_snapshots (race_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_indycar_snapshots_race_athlete ON indycar_race_snapshots (race_id, espn_athlete_id, timestamp);
