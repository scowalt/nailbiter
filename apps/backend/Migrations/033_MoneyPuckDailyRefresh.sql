CREATE TABLE IF NOT EXISTS moneypuck_refresh_state (
    id INTEGER PRIMARY KEY CONSTRAINT ck_moneypuck_refresh_singleton CHECK (id = 1),
    next_attempt_at_ticks BIGINT NOT NULL CONSTRAINT ck_moneypuck_refresh_ticks CHECK (next_attempt_at_ticks BETWEEN 1 AND 3155378975999999999),
    active BOOLEAN NOT NULL,
    denied BOOLEAN NOT NULL
);
