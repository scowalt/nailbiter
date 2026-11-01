
ALTER TABLE games ADD COLUMN IF NOT EXISTS season_year INTEGER;
ALTER TABLE games ADD COLUMN IF NOT EXISTS season_type INTEGER;
ALTER TABLE games ADD COLUMN IF NOT EXISTS week_number INTEGER;
ALTER TABLE games ADD COLUMN IF NOT EXISTS week_label VARCHAR(50);

CREATE INDEX IF NOT EXISTS idx_games_week ON games(sport, season_year, season_type, week_number);
