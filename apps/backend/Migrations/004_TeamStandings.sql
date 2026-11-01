CREATE TABLE IF NOT EXISTS team_standings (
    id SERIAL PRIMARY KEY,
    sport VARCHAR(20) NOT NULL,
    team_abbreviation VARCHAR(10) NOT NULL,
    team_name VARCHAR(100),
    division VARCHAR(50),
    conference VARCHAR(50),
    division_rank INTEGER,
    conference_rank INTEGER,
    national_rank INTEGER,
    ranking_poll VARCHAR(20),
    season INTEGER NOT NULL,
    fetched_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_team_standings_sport ON team_standings(sport);
CREATE INDEX IF NOT EXISTS idx_team_standings_lookup ON team_standings(sport, team_abbreviation, season);
CREATE INDEX IF NOT EXISTS idx_team_standings_fetched ON team_standings(fetched_at);
