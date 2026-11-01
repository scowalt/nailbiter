ALTER TABLE team_ratings
    ADD COLUMN IF NOT EXISTS playoff_probability_is_final BOOLEAN NOT NULL DEFAULT FALSE;
