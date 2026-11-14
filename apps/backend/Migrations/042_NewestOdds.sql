BEGIN;

CREATE OR REPLACE FUNCTION newest_odds_track() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    newest timestamptz;
    current_newest timestamptz;
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        SELECT timestamp INTO current_newest FROM newest_odds WHERE game_id = OLD.game_id FOR UPDATE;
        IF current_newest = OLD.timestamp THEN
            SELECT max(timestamp) INTO newest FROM odds_snapshots WHERE game_id = OLD.game_id;
            IF newest IS NULL THEN
                DELETE FROM newest_odds WHERE game_id = OLD.game_id;
            ELSE
                UPDATE newest_odds SET timestamp = newest WHERE game_id = OLD.game_id;
            END IF;
        END IF;
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        INSERT INTO newest_odds AS newest_row (game_id, timestamp) VALUES (NEW.game_id, NEW.timestamp)
        ON CONFLICT (game_id) DO UPDATE SET timestamp = excluded.timestamp WHERE newest_row.timestamp < excluded.timestamp;
    END IF;
    RETURN NULL;
END $$;

DO $$
BEGIN
    IF to_regclass('newest_odds') IS NULL THEN
        CREATE TABLE newest_odds (
            game_id text PRIMARY KEY,
            timestamp timestamptz NOT NULL
        );
        CREATE TRIGGER newest_odds_track AFTER INSERT OR DELETE OR UPDATE OF game_id, timestamp ON odds_snapshots
            FOR EACH ROW EXECUTE FUNCTION newest_odds_track();
        INSERT INTO newest_odds (game_id, timestamp) SELECT game_id, max(timestamp) FROM odds_snapshots GROUP BY game_id;
    END IF;
END $$;

COMMIT;
