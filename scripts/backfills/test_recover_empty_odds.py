import copy
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

from recover_empty_odds import instant, prepare, render_sql, samples, traded_closes, validate_game, validate_settlement


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.game = {
            "id": "401872923", "sport": "nfl", "home_team_abbr": "DET", "away_team_abbr": "NO",
            "scheduled_start": "2026-09-13T17:00:00Z", "first_play": "2026-09-13T17:00:00Z",
            "last_play": "2026-09-13T17:03:00Z", "expected_points": 2,
            "home_ticker": "KXNFLGAME-26SEP13NODET-DET", "away_ticker": "KXNFLGAME-26SEP13NODET-NO",
        }
        self.manifest = {"batch_id": "test_recovery", "games": [self.game]}
        self.start, self.end = instant(self.game["first_play"]), instant(self.game["last_play"])
        self.stamp = int(self.start.timestamp())
        self.summary = {"header": {"competitions": [{
            "id": self.game["id"], "date": self.game["scheduled_start"],
            "status": {"type": {"name": "STATUS_FINAL", "completed": True}},
            "competitors": [{"homeAway": "home", "winner": True, "team": {"abbreviation": "DET"}},
                            {"homeAway": "away", "winner": False, "team": {"abbreviation": "NO"}}],
        }]}, "drives": {"previous": [{"id": "1", "plays": [
            {"wallclock": self.game["first_play"], "period": {"number": 1}},
            {"wallclock": self.game["last_play"], "period": {"number": 4}},
        ]}]}}

    def payload(self, side="home", price="0.7000"):
        return {"ticker": self.game[side + "_ticker"], "candlesticks": [
            {"end_period_ts": self.stamp + seconds, "price": {"close_dollars": price}, "volume_fp": "10.00"}
            for seconds in [0, 60, 120, 180]]}

    def market(self, side="home"):
        ticker = self.game[side + "_ticker"]
        return {"ticker": ticker, "event_ticker": ticker.rsplit("-", 1)[0], "status": "finalized",
                "result": "yes" if side == "home" else "no", "created_time": "2026-09-12T00:00:00Z",
                "close_time": "2026-09-13T17:05:00Z"}

    def test_reviewed_final_whistle_excludes_bad_administrative_dates(self):
        self.summary["drives"]["previous"][0]["plays"] += [
            {"wallclock": "2026-09-14T17:01:00Z", "period": {"number": 1}},
            {"id": "end", "wallclock": self.game["last_play"], "type": {"text": "End of Game"},
             "period": {"number": 4}, "clock": {"displayValue": "0:00"}},
        ]
        with self.assertRaises(ValueError): validate_game(self.game, self.summary)
        self.game.update(end_play_id="end", play_window_note="Independently reviewed wrong-day administrative records")
        validate_game(self.game, self.summary)
        del self.game["play_window_note"]
        with self.assertRaises(ValueError): validate_game(self.game, self.summary)

    def test_settlement_must_agree_with_winner_and_play_window(self):
        validate_settlement(self.game, self.summary, [self.market(), self.market("away")])
        for key, value in [("result", "no"), ("status", "active"), ("event_ticker", "wrong-game"),
                           ("close_time", "2026-09-14T17:05:00Z"), ("created_time", "2026-09-14T00:00:00Z")]:
            with self.subTest(key=key):
                market = self.market(); market[key] = value
                with self.assertRaises(ValueError): validate_settlement(self.game, self.summary, [market, self.market("away")])

    def test_valid_identity_and_football_play_bounds(self):
        validate_game(self.game, self.summary)

    def test_rejects_identity_and_status_changes(self):
        for key, value in [("id", "1"), ("date", "2026-09-14T17:00:00Z"),
                           ("status", {"type": {"name": "STATUS_POSTPONED", "completed": False}})]:
            with self.subTest(key=key):
                summary = copy.deepcopy(self.summary)
                summary["header"]["competitions"][0][key] = value
                with self.assertRaises(ValueError): validate_game(self.game, summary)

    def test_rejects_changed_teams(self):
        self.summary["header"]["competitions"][0]["competitors"][0]["team"]["abbreviation"] = "BAL"
        with self.assertRaises(ValueError): validate_game(self.game, self.summary)

    def test_rejects_cross_event_outcomes_and_wrong_team_codes(self):
        for ticker in ["KXNFLGAME-26SEP13NOBAL-NO", "KXNFLGAME-26SEP13NODET-NOP"]:
            with self.subTest(ticker=ticker):
                self.game["away_ticker"] = ticker
                with self.assertRaises(ValueError): validate_game(self.game, self.summary)

    def test_rejects_changed_play_bounds(self):
        self.summary["drives"]["previous"][0]["plays"][0]["wallclock"] = "2026-09-13T17:01:00Z"
        with self.assertRaises(ValueError): validate_game(self.game, self.summary)

    def test_rejects_missing_timezone(self):
        with self.assertRaises(ValueError): instant("2026-09-13T17:00:00")

    def test_requires_explicit_reschedule_evidence(self):
        self.game["home_ticker"] = "KXNFLGAME-26SEP131330NODET-DET"
        self.game["away_ticker"] = "KXNFLGAME-26SEP131330NODET-NO"
        with self.assertRaises(ValueError): validate_game(self.game, self.summary)
        self.game["selection_note"] = "Explicitly audited event identity, not nearest-time matching"
        validate_game(self.game, self.summary)

    def test_skips_pregame_and_postgame_candles(self):
        closes = traded_closes(self.payload(), self.game["home_ticker"], self.start, self.end)
        self.assertEqual([self.stamp + 60, self.stamp + 120], sorted(closes))

    def test_skips_candle_overlapping_first_play(self):
        start = datetime.fromtimestamp(self.stamp + 1, timezone.utc)
        closes = traded_closes(self.payload(), self.game["home_ticker"], start, self.end)
        self.assertEqual([self.stamp + 120], sorted(closes))

    def test_requires_exact_response_ticker(self):
        with self.assertRaises(ValueError):
            traded_closes(self.payload("away"), self.game["home_ticker"], self.start, self.end)

    def test_skips_invalid_terminal_prices_and_empty_volume(self):
        for field, value in [("price", {"close_dollars": x}) for x in
                             ["0", "1", "-0.1", "1.1", "NaN", "Infinity", "invalid", None]] + [
                                 ("volume_fp", x) for x in ["0", "-1", "NaN", "Infinity", None]]:
            with self.subTest(field=field, value=value):
                data = self.payload()
                for c in data["candlesticks"]: c[field] = value
                self.assertEqual({}, traded_closes(data, self.game["home_ticker"], self.start, self.end))

    def test_skips_duplicate_and_non_minute_timestamps(self):
        data = self.payload()
        data["candlesticks"].append(copy.deepcopy(data["candlesticks"][1]))
        data["candlesticks"][2]["end_period_ts"] += 1
        self.assertEqual({}, traded_closes(data, self.game["home_ticker"], self.start, self.end))

    def test_legacy_price_and_volume_supported(self):
        data = self.payload()
        for c in data["candlesticks"]:
            c["price"] = {"close": 70}
            del c["volume_fp"]
            c["volume"] = 10
        self.assertEqual(2, len(traded_closes(data, self.game["home_ticker"], self.start, self.end)))

    def test_pairs_only_contemporaneous_trades_and_normalizes(self):
        rows = samples(self.game, self.payload(), self.payload("away", "0.4000"))
        self.assertEqual(2, len(rows))
        self.assertAlmostEqual(7 / 11, float(rows[0][4]), places=11)
        self.assertAlmostEqual(1, float(rows[0][4]) + float(rows[0][5]))
        self.assertEqual((-175, 175), rows[0][2:4])
        away = self.payload("away")
        away["candlesticks"][1]["volume_fp"] = "0"
        with self.assertRaises(ValueError): samples(self.game, self.payload(), away)

    def test_prepares_evidence_hashes_and_rejects_duplicate_games(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            for suffix, payload in [("espn", self.summary), ("home-candles", self.payload()),
                                    ("away-candles", self.payload("away", "0.3")),
                                    ("home-market", self.market()), ("away-market", self.market("away"))]:
                (path / f"{self.game['id']}-{suffix}.json").write_text(json.dumps(payload))
            rows, hashes = prepare(self.manifest, path)
            self.assertEqual(2, len(rows))
            self.assertEqual(5, len(hashes))
            self.manifest["games"].append(self.game)
            with self.assertRaises(ValueError): prepare(self.manifest, path)

    def test_sql_defaults_to_rollback_and_never_updates_live_data(self):
        rows = samples(self.game, self.payload(), self.payload("away", "0.3"))
        sql = render_sql(self.manifest, rows)
        self.assertTrue(sql.endswith("ROLLBACK;\n"))
        self.assertNotIn("DELETE FROM", sql)
        self.assertIn("FOR UPDATE OF g", sql)
        self.assertIn("Approved game already has snapshots", sql)
        self.assertIn("'test_recovery',true FROM recovery_points", sql)
        self.assertTrue(render_sql(self.manifest, rows, commit=True).endswith("COMMIT;\n"))
        undo = render_sql(self.manifest, rows, undo=True)
        self.assertTrue(undo.endswith("ROLLBACK;\n"))
        self.assertIn("Recovery batch changed; refusing automatic rollback", undo)
        self.assertIn("o.source='test_recovery' AND o.is_historical", undo)

    def test_sql_rejects_duplicate_points_and_source_injection(self):
        rows = samples(self.game, self.payload(), self.payload("away", "0.3"))
        with self.assertRaises(ValueError): render_sql(self.manifest, [rows[0], rows[0]])
        self.manifest["batch_id"] = "bad'; DELETE FROM games; --"
        with self.assertRaises(ValueError): render_sql(self.manifest, rows)


if __name__ == "__main__":
    unittest.main()
