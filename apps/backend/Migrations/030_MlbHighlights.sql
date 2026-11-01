ALTER TABLE games
    ADD COLUMN IF NOT EXISTS espn_completion_evidence_json text,
    ADD COLUMN IF NOT EXISTS espn_completion_observed_at timestamptz,
    ADD COLUMN IF NOT EXISTS completion_confirmed_at timestamptz,
    ADD COLUMN IF NOT EXISTS highlights_discovery_expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS mlb_game_pk bigint,
    ADD COLUMN IF NOT EXISTS mlb_official_date date,
    ADD COLUMN IF NOT EXISTS highlights_identity_evidence_json text;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_games_mlb_game_pk" ON games (mlb_game_pk);

CREATE TABLE IF NOT EXISTS game_highlights (
    game_id text PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
    selected_video_id text,
    ownership text,
    suppressed boolean NOT NULL DEFAULT false,
    suppressed_at timestamptz,
    selected_at timestamptz,
    availability text NOT NULL DEFAULT 'Unknown',
    unavailable_reason text,
    title text,
    channel_id text,
    published_at timestamptz,
    made_for_kids boolean,
    allowed_regions text[],
    blocked_regions text[],
    selection_evidence_json text,
    validated_at timestamptz,
    provider_data_refreshed_at timestamptz,
    provider_data_expires_at timestamptz,
    CONSTRAINT ck_highlights_ownership CHECK (ownership IS NULL OR ownership IN ('Automatic', 'Manual')),
    CONSTRAINT ck_highlights_availability CHECK (availability IN ('Unknown', 'Playable', 'Unavailable')),
    CONSTRAINT ck_highlights_selection_owner CHECK (selected_video_id IS NULL OR ownership IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS highlights_operations (
    id integer PRIMARY KEY,
    launch_boundary_at timestamptz,
    catchup_window_start timestamptz,
    catchup_cursor_game_id text,
    catchup_completed_at timestamptz,
    uploads_playlist_id text,
    uploads_page_token text,
    provider_data_refreshed_at timestamptz,
    provider_data_expires_at timestamptz,
    last_poll_attempt_at timestamptz,
    next_poll_at timestamptz,
    backoff_until timestamptz,
    quota_day date,
    quota_units_used integer NOT NULL DEFAULT 0,
    consecutive_failures integer NOT NULL DEFAULT 0,
    last_reason text,
    CONSTRAINT ck_highlights_operations_singleton CHECK (id = 1),
    CONSTRAINT ck_highlights_operations_quota CHECK (quota_units_used BETWEEN 0 AND 1000 AND consecutive_failures >= 0),
    CONSTRAINT ck_highlights_operations_window CHECK (
        (launch_boundary_at IS NULL AND catchup_window_start IS NULL)
        OR (launch_boundary_at IS NOT NULL AND catchup_window_start IS NOT NULL
            AND catchup_window_start = launch_boundary_at - INTERVAL '7 days'))
);

INSERT INTO highlights_operations (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
