import os
import subprocess
import time
import unittest
import uuid

import test_recover_empty_odds as fixtures
from recover_empty_odds import render_sql, samples


@unittest.skipUnless(os.environ.get("RUN_RECOVERY_DB_TESTS") == "1", "Set RUN_RECOVERY_DB_TESTS=1 for isolated TimescaleDB tests")
class RecoveryDatabaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.container = "nailbiter-backfill-test-" + uuid.uuid4().hex[:10]
        subprocess.run(["docker", "run", "--detach", "--network", "none", "--name", cls.container,
                        "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "timescale/timescaledb:2.26.3-pg16"],
                       check=True, capture_output=True)
        cls.addClassCleanup(lambda: subprocess.run(["docker", "rm", "-f", cls.container], check=True, capture_output=True))
        for _ in range(60):
            result = subprocess.run(["docker", "exec", cls.container, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"], capture_output=True)
            if result.returncode == 0: break
            time.sleep(.5)
        else: raise RuntimeError("Isolated test database did not start")

    def sql(self, text, succeeds=True):
        result = subprocess.run(["docker", "exec", "-i", self.container, "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1", "-U", "postgres"],
                                input=text, text=True, capture_output=True)
        if succeeds: self.assertEqual(0, result.returncode, result.stderr)
        else: self.assertNotEqual(0, result.returncode, result.stdout)
        return result.stdout.strip() if succeeds else result.stderr

    def setUp(self):
        fixture = fixtures.RecoveryTests(); fixture.setUp()
        self.manifest = fixture.manifest
        self.rows = samples(fixture.game, fixture.payload(), fixture.payload("away", "0.3"))
        self.sql("""
CREATE EXTENSION IF NOT EXISTS timescaledb;
DROP TABLE IF EXISTS odds_snapshots CASCADE;
DROP TABLE IF EXISTS games CASCADE;
CREATE TABLE games (id text PRIMARY KEY, sport text, home_team_abbr text, away_team_abbr text,
 scheduled_start timestamptz, status text, is_historical_import boolean DEFAULT false);
CREATE TABLE odds_snapshots (
 id bigserial, game_id text REFERENCES games(id), timestamp timestamptz,
 home_moneyline integer NOT NULL, away_moneyline integer NOT NULL,
 home_win_probability double precision NOT NULL, away_win_probability double precision NOT NULL,
 home_score integer, away_score integer, period integer, game_clock text,
 source varchar(50), is_historical boolean NOT NULL DEFAULT false,
 spread double precision, over_under double precision, draw_moneyline integer, draw_win_probability double precision,
 PRIMARY KEY(id,timestamp));
SELECT create_hypertable('odds_snapshots','timestamp',chunk_time_interval=>interval '1 day');
ALTER TABLE odds_snapshots SET (timescaledb.compress,timescaledb.compress_segmentby='game_id');
INSERT INTO games VALUES ('401872923','nfl','DET','NO','2026-09-13T17:00Z','final',false),
 ('control','nfl','ATL','BAL','2026-09-13T17:00Z','final',false);
INSERT INTO odds_snapshots (game_id,timestamp,home_moneyline,away_moneyline,home_win_probability,away_win_probability,source)
 VALUES ('control','2026-09-13T17:01Z',-100,100,0.5,0.5,'Kalshi');
SELECT compress_chunk(c) FROM show_chunks('odds_snapshots') c;
""")
        self.control = self.sql("SELECT row_to_json(o) FROM odds_snapshots o WHERE game_id='control';")

    def assert_control_unchanged(self):
        self.assertEqual(self.control, self.sql("SELECT row_to_json(o) FROM odds_snapshots o WHERE game_id='control';"))

    def test_dry_run_leaves_no_history_in_compressed_chunk(self):
        self.sql(render_sql(self.manifest, self.rows))
        self.assertEqual("0", self.sql("SELECT count(*) FROM odds_snapshots WHERE game_id='401872923';"))
        self.assert_control_unchanged()

    def test_commit_is_historical_duplicate_refused_and_undo_preserves_live(self):
        self.sql(render_sql(self.manifest, self.rows, commit=True))
        self.assertEqual("2|0", self.sql("SELECT count(*),count(*) FILTER (WHERE NOT is_historical OR period IS NOT NULL OR home_score IS NOT NULL OR game_clock IS NOT NULL) FROM odds_snapshots WHERE game_id='401872923';"))
        error = self.sql(render_sql(self.manifest, self.rows, commit=True), succeeds=False)
        self.assertIn("already has snapshots", error)
        self.assertEqual("2", self.sql("SELECT count(*) FROM odds_snapshots WHERE source='test_recovery';"))
        self.sql(render_sql(self.manifest, self.rows, commit=True, undo=True))
        self.assertEqual("0", self.sql("SELECT count(*) FROM odds_snapshots WHERE source='test_recovery';"))
        self.assert_control_unchanged()

    def test_changed_game_refuses_all_writes(self):
        self.sql("UPDATE games SET status='scheduled' WHERE id='401872923';")
        error = self.sql(render_sql(self.manifest, self.rows, commit=True), succeeds=False)
        self.assertIn("Game identity/status changed", error)
        self.assertEqual("0", self.sql("SELECT count(*) FROM odds_snapshots WHERE game_id='401872923';"))
        self.assert_control_unchanged()

    def test_any_existing_snapshot_refuses_import(self):
        self.sql("INSERT INTO odds_snapshots (game_id,timestamp,home_moneyline,away_moneyline,home_win_probability,away_win_probability,source) VALUES ('401872923','2026-09-13T17:01Z',-100,100,0.5,0.5,'Kalshi');")
        error = self.sql(render_sql(self.manifest, self.rows, commit=True), succeeds=False)
        self.assertIn("already has snapshots", error)
        self.assertEqual("1|0", self.sql("SELECT count(*),count(*) FILTER (WHERE is_historical) FROM odds_snapshots WHERE game_id='401872923';"))
        self.assert_control_unchanged()

    def test_changed_batch_refuses_automatic_undo(self):
        self.sql(render_sql(self.manifest, self.rows, commit=True))
        self.sql("UPDATE odds_snapshots SET home_score=7 WHERE source='test_recovery';")
        error = self.sql(render_sql(self.manifest, self.rows, commit=True, undo=True), succeeds=False)
        self.assertIn("Recovery batch changed", error)
        self.assertEqual("2", self.sql("SELECT count(*) FROM odds_snapshots WHERE source='test_recovery';"))
        self.assert_control_unchanged()


if __name__ == "__main__":
    unittest.main()
