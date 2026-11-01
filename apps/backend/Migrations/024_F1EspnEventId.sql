BEGIN;

ALTER TABLE f1_races
    ADD COLUMN IF NOT EXISTS espn_event_id TEXT;

UPDATE f1_races
SET espn_event_id = id
WHERE espn_event_id IS NULL;

ALTER TABLE f1_races
    ALTER COLUMN espn_event_id SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_f1_races_espn_event_id
    ON f1_races (espn_event_id);

COMMIT;
