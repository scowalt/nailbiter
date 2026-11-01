
BEGIN;

ALTER TABLE indycar_races
    ADD COLUMN IF NOT EXISTS espn_event_id TEXT;

UPDATE indycar_races
SET espn_event_id = id
WHERE espn_event_id IS NULL;

ALTER TABLE indycar_races
    ALTER COLUMN espn_event_id SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_indycar_races_espn_event_id
    ON indycar_races (espn_event_id);

COMMIT;
