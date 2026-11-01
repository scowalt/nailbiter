
CREATE TABLE IF NOT EXISTS team_ratings (
    id SERIAL PRIMARY KEY,
    sport VARCHAR(10) NOT NULL,
    team_abbreviation VARCHAR(10) NOT NULL,
    team_name VARCHAR(100),

    elo_rating DOUBLE PRECISION,
    bpi_rating DOUBLE PRECISION,
    fpi_rating DOUBLE PRECISION,
    power_rank INTEGER,

    playoff_probability DOUBLE PRECISION,
    championship_probability DOUBLE PRECISION,

    game_quality DOUBLE PRECISION,       
    game_importance DOUBLE PRECISION,    

    source VARCHAR(50) NOT NULL,   
    season INTEGER NOT NULL,
    fetched_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(sport, team_abbreviation, season, source)
);

CREATE INDEX IF NOT EXISTS idx_team_ratings_sport ON team_ratings(sport);
CREATE INDEX IF NOT EXISTS idx_team_ratings_lookup ON team_ratings(sport, team_abbreviation, season);
CREATE INDEX IF NOT EXISTS idx_team_ratings_source ON team_ratings(source);
CREATE INDEX IF NOT EXISTS idx_team_ratings_fetched ON team_ratings(fetched_at);
