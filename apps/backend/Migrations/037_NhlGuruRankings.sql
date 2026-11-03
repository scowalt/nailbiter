CREATE TABLE IF NOT EXISTS nhl_guru_refresh_state (
    id INTEGER PRIMARY KEY CONSTRAINT ck_nhl_guru_singleton CHECK (id = 1),
    next_attempt_at_ticks BIGINT NOT NULL CONSTRAINT ck_nhl_guru_ticks CHECK (next_attempt_at_ticks BETWEEN 1 AND 3155378975999999999)
);
