
ALTER TABLE team_standings ADD COLUMN IF NOT EXISTS wins INTEGER;
ALTER TABLE team_standings ADD COLUMN IF NOT EXISTS losses INTEGER;
ALTER TABLE team_standings ADD COLUMN IF NOT EXISTS ties INTEGER;

CREATE INDEX IF NOT EXISTS idx_team_standings_record ON team_standings(sport, season, wins, losses);
