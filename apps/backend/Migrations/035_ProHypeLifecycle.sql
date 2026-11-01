CREATE TABLE IF NOT EXISTS pro_hype_snapshots (
    game_id text PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
    last_evaluation_ticks bigint NOT NULL,
    evidence_revision bigint NOT NULL,
    confirmed_started boolean NOT NULL DEFAULT false,
    snapshot_json text,
    revoked_evidence_json text NOT NULL DEFAULT '[]'
);

ALTER TABLE pro_hype_snapshots ADD COLUMN IF NOT EXISTS confirmed_started boolean NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS pro_hype_revision (
    id integer PRIMARY KEY CHECK (id = 1),
    revision bigint NOT NULL DEFAULT 0
);
INSERT INTO pro_hype_revision (id, revision) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS pro_hype_evidence_changes (
    revision bigint PRIMARY KEY,
    kind text NOT NULL,
    before_subject text,
    after_subject text,
    before_json text,
    after_json text
);

ALTER TABLE pro_hype_evidence_changes ADD COLUMN IF NOT EXISTS before_subject text;
ALTER TABLE pro_hype_evidence_changes ADD COLUMN IF NOT EXISTS after_subject text;
CREATE INDEX IF NOT EXISTS ix_pro_hype_changes_before_subject_revision ON pro_hype_evidence_changes (before_subject, revision);
CREATE INDEX IF NOT EXISTS ix_pro_hype_changes_after_subject_revision ON pro_hype_evidence_changes (after_subject, revision);

CREATE OR REPLACE FUNCTION pro_hype_subject(kind text, value jsonb) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN value IS NULL THEN NULL
        WHEN kind = 'games' THEN 'game:' || (value->>'id')
        WHEN kind = 'pregame_odds' THEN 'game:' || (value->>'game_id')
        ELSE 'team:' || (value->>'sport') || ':' || (value->>'season') || ':' ||
            CASE WHEN value->>'sport' = 'nhl' AND value->>'team_abbreviation' = 'UTA' THEN 'UTAH'
                ELSE value->>'team_abbreviation' END
    END;
$$;

CREATE OR REPLACE FUNCTION pro_hype_fence_write() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM id FROM pro_hype_revision WHERE id = 1 FOR UPDATE;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION pro_hype_record_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    before_value jsonb;
    after_value jsonb;
    before_support jsonb;
    after_support jsonb;
    fields text[];
    next_revision bigint;
BEGIN
    IF TG_OP <> 'INSERT' THEN before_value := to_jsonb(OLD); END IF;
    IF TG_OP <> 'DELETE' THEN after_value := to_jsonb(NEW); END IF;
    IF TG_TABLE_NAME = 'games' THEN
        fields := ARRAY['id','sport','season_year','season_type','home_team_id','away_team_id','home_team_abbr',
            'away_team_abbr','scheduled_start','actual_start','status','is_historical_import','pro_evidence_json'];
    ELSIF TG_TABLE_NAME = 'team_ratings' THEN
        fields := ARRAY['sport','season','source','team_abbreviation','source_season','power_rank','rank_published_at',
            'playoff_probability','playoff_probability_is_final','playoff_evidence_json','fetched_at'];
    ELSE
        fields := ARRAY['game_id','source','source_season','applicable_start','home_team_abbreviation','away_team_abbreviation',
            'home_win_probability','away_win_probability','draw_win_probability','fetched_at'];
    END IF;
    SELECT jsonb_object_agg(key, value) INTO before_support FROM jsonb_each(before_value) WHERE key = ANY(fields);
    SELECT jsonb_object_agg(key, value) INTO after_support FROM jsonb_each(after_value) WHERE key = ANY(fields);
    IF before_support IS NOT DISTINCT FROM after_support THEN RETURN NULL; END IF;
    UPDATE pro_hype_revision SET revision = revision + 1 WHERE id = 1 RETURNING revision INTO next_revision;
    INSERT INTO pro_hype_evidence_changes (revision, kind, before_subject, after_subject, before_json, after_json)
        VALUES (next_revision, TG_TABLE_NAME, pro_hype_subject(TG_TABLE_NAME, before_value),
            pro_hype_subject(TG_TABLE_NAME, after_value), before_value::text, after_value::text);
    RETURN NULL;
END;
$$;

CREATE OR REPLACE TRIGGER pro_hype_games_fence BEFORE INSERT OR UPDATE OR DELETE ON games
    FOR EACH STATEMENT EXECUTE FUNCTION pro_hype_fence_write();
CREATE OR REPLACE TRIGGER pro_hype_games_revision AFTER INSERT OR UPDATE OR DELETE ON games
    FOR EACH ROW EXECUTE FUNCTION pro_hype_record_change();
CREATE OR REPLACE TRIGGER pro_hype_ratings_fence BEFORE INSERT OR UPDATE OR DELETE ON team_ratings
    FOR EACH STATEMENT EXECUTE FUNCTION pro_hype_fence_write();
CREATE OR REPLACE TRIGGER pro_hype_ratings_revision AFTER INSERT OR UPDATE OR DELETE ON team_ratings
    FOR EACH ROW EXECUTE FUNCTION pro_hype_record_change();
CREATE OR REPLACE TRIGGER pro_hype_odds_fence BEFORE INSERT OR UPDATE OR DELETE ON pregame_odds
    FOR EACH STATEMENT EXECUTE FUNCTION pro_hype_fence_write();
CREATE OR REPLACE TRIGGER pro_hype_odds_revision AFTER INSERT OR UPDATE OR DELETE ON pregame_odds
    FOR EACH ROW EXECUTE FUNCTION pro_hype_record_change();
