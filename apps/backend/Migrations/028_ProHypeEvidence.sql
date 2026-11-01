ALTER TABLE games ADD COLUMN IF NOT EXISTS pro_evidence_json text;
ALTER TABLE pregame_odds ADD COLUMN IF NOT EXISTS source_season integer;
ALTER TABLE pregame_odds ADD COLUMN IF NOT EXISTS applicable_start timestamptz;
ALTER TABLE pregame_odds ADD COLUMN IF NOT EXISTS home_team_abbreviation text;
ALTER TABLE pregame_odds ADD COLUMN IF NOT EXISTS away_team_abbreviation text;
