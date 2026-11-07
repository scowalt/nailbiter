CREATE TABLE IF NOT EXISTS start_hype (
    game_id text PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
    total double precision NOT NULL,
    quality_source text,
    moneypuck_teams text[] NOT NULL DEFAULT '{}'
);
