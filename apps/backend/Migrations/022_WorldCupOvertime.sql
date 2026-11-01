
BEGIN;

ALTER TABLE games
    ADD COLUMN IF NOT EXISTS is_overtime BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE games AS game
SET is_overtime = TRUE
WHERE game.sport = 'world_cup'
  AND game.status = 'in_progress'
  AND EXISTS (
      SELECT 1
      FROM odds_snapshots AS snapshot
      WHERE snapshot.game_id = game.id
        AND snapshot.period >= 3
  );

COMMIT;
