CREATE TABLE IF NOT EXISTS nascar_race_results (
    id SERIAL PRIMARY KEY,
    race_id TEXT NOT NULL REFERENCES nascar_races(id) ON DELETE CASCADE,
    espn_athlete_id TEXT NOT NULL,
    display_name TEXT NOT NULL,
    short_name TEXT NOT NULL,
    finish_position INTEGER,
    winner BOOLEAN NOT NULL DEFAULT FALSE,
    country_flag_href TEXT
);

CREATE INDEX IF NOT EXISTS idx_nascar_race_results_race_id ON nascar_race_results (race_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_nascar_race_results_race_athlete ON nascar_race_results (race_id, espn_athlete_id);
