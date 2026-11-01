ALTER TABLE highlights_operations ADD COLUMN IF NOT EXISTS provider_configuration_rejected boolean NOT NULL DEFAULT false;
UPDATE highlights_operations SET provider_configuration_rejected = true WHERE last_reason IN ('InvalidConfiguration', 'ChannelUnverified');
ALTER TABLE highlights_operations ADD COLUMN IF NOT EXISTS next_validation_attempt_at timestamptz;
ALTER TABLE highlights_operations ADD COLUMN IF NOT EXISTS worker_reserved_units integer NOT NULL DEFAULT 0 CONSTRAINT ck_highlights_worker_reservation CHECK (worker_reserved_units BETWEEN 0 AND 10);
ALTER TABLE highlights_operations ADD COLUMN IF NOT EXISTS catchup_remaining_game_ids text[];
ALTER TABLE highlights_operations ADD COLUMN IF NOT EXISTS catchup_pages_scanned integer NOT NULL DEFAULT 0 CONSTRAINT ck_highlights_catchup_pages CHECK (catchup_pages_scanned BETWEEN 0 AND 16);
