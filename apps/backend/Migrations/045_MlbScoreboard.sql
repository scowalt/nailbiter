ALTER TABLE games ADD COLUMN IF NOT EXISTS mlb_scoreboard_json text, ADD COLUMN IF NOT EXISTS scoreboard_source_revision_at timestamptz;
