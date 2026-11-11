CREATE TABLE IF NOT EXISTS playoffstatus_collection_state (
    league TEXT PRIMARY KEY CONSTRAINT ck_playoffstatus_collection_league CHECK (league IN ('nfl', 'nba', 'mlb')),
    next_attempt_at_ticks BIGINT NOT NULL CONSTRAINT ck_playoffstatus_collection_ticks CHECK (next_attempt_at_ticks BETWEEN 1 AND 3155378975999999999)
);
