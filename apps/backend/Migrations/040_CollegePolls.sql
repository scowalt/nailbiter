ALTER TABLE college_teams ADD COLUMN IF NOT EXISTS poll_rank integer;
ALTER TABLE college_teams ADD COLUMN IF NOT EXISTS poll_updated_at timestamptz;
ALTER TABLE college_teams ADD COLUMN IF NOT EXISTS poll text;
ALTER TABLE games ADD COLUMN IF NOT EXISTS home_poll_rank integer;
ALTER TABLE games ADD COLUMN IF NOT EXISTS away_poll_rank integer;
ALTER TABLE games ADD COLUMN IF NOT EXISTS rank_poll text;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'college_teams' AND column_name = 'ap_rank') THEN
        UPDATE college_teams SET poll_rank = ap_rank, poll_updated_at = ap_updated_at, poll = 'ap'
        WHERE poll IS NULL AND (ap_rank IS NOT NULL OR ap_updated_at IS NOT NULL);
        UPDATE college_teams SET ap_rank = NULL, ap_updated_at = NULL
        WHERE ap_rank IS NOT NULL OR ap_updated_at IS NOT NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'games' AND column_name = 'home_ap_rank') THEN
        UPDATE games SET home_poll_rank = home_ap_rank, away_poll_rank = away_ap_rank, rank_poll = 'ap'
        WHERE rank_poll IS NULL AND (home_ap_rank IS NOT NULL OR away_ap_rank IS NOT NULL);
        UPDATE games SET home_ap_rank = NULL, away_ap_rank = NULL
        WHERE home_ap_rank IS NOT NULL OR away_ap_rank IS NOT NULL;
    END IF;
END
$$;

ALTER TABLE college_teams DROP CONSTRAINT IF EXISTS ck_college_teams_poll;
ALTER TABLE college_teams ADD CONSTRAINT ck_college_teams_poll CHECK (poll IS NULL OR poll IN ('ap', 'cfp'));
ALTER TABLE games DROP CONSTRAINT IF EXISTS ck_games_rank_poll;
ALTER TABLE games ADD CONSTRAINT ck_games_rank_poll CHECK (rank_poll IS NULL OR rank_poll IN ('ap', 'cfp'));
