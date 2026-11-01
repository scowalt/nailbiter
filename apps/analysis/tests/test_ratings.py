
import ast
import contextlib
import io
import json
import unittest
from datetime import UTC, datetime, timedelta
from pathlib import Path
from unittest.mock import patch

import pandas as pd
from sqlalchemy import create_engine, text

from hype_score.config import HypeConfig
from hype_score.database import load_games, load_team_ratings, load_team_ratings_for_games
from hype_score.models import Game, PregameOdds, TeamRating
from hype_score.ratings import RATING_COLUMNS, playoff_probability
from hype_score.scoring import calculate_hype_score, calculate_hype_scores_batch, calculate_stakes

NOW = datetime(2026, 9, 14, 18, tzinfo=UTC)
NOTEBOOKS = Path(__file__).resolve().parents[1] / "notebooks"


def rating(**changes):
    row = dict(
        sport="nfl",
        team_abbreviation="KC",
        season=2026,
        elo_rating=None,
        bpi_rating=None,
        fpi_rating=None,
        championship_probability=None,
        playoff_probability=0.63,
        playoff_probability_is_final=False,
        source="kalshi",
        source_season=2026,
        fetched_at=NOW,
    )
    row.update(changes)
    return row


def game(**changes):
    row = dict(
        id="game",
        sport="nfl",
        status="scheduled",
        season_year=2026,
        season_type=2,
        home_team="Chiefs",
        away_team="Bills",
        home_team_abbreviation="KC",
        away_team_abbreviation="BUF",
        scheduled_start=NOW + timedelta(hours=2),
        home_score=None,
        away_score=None,
    )
    row.update(changes)
    return row


def odds(game_id="game"):
    return dict(game_id=game_id, home_win_probability=0.5, away_win_probability=0.5)


class RatingSelectionTests(unittest.TestCase):
    def setUp(self):
        self.engine = create_engine("sqlite:///:memory:")
        self.addCleanup(self.engine.dispose)
        with self.engine.begin() as connection:
            connection.execute(
                text("""
                CREATE TABLE team_ratings (
                    id INTEGER PRIMARY KEY,
                    sport TEXT, team_abbreviation TEXT, season INTEGER,
                    elo_rating REAL, bpi_rating REAL, fpi_rating REAL,
                    championship_probability REAL, playoff_probability REAL,
                    playoff_probability_is_final BOOLEAN NOT NULL DEFAULT false,
                    source TEXT, source_season INTEGER, fetched_at TEXT
                )
            """)
            )

    def add(self, **changes):
        row = rating(**changes)
        if row["fetched_at"] is not None:
            row["fetched_at"] = row["fetched_at"].isoformat()
        fields = ", ".join(row)
        values = ", ".join(f":{field}" for field in row)
        with self.engine.begin() as connection:
            connection.execute(text(f"INSERT INTO team_ratings ({fields}) VALUES ({values})"), row)

    def load(self, **filters):
        return load_team_ratings(self.engine, evaluated_at=NOW, **filters)

    def test_all_four_sports_are_kalshi_only(self):
        for sport in ("nfl", "nba", "nhl", "mlb"):
            self.add(sport=sport)
            self.add(
                sport=sport,
                source="powerrankingsguru",
                playoff_probability=0.99,
                fetched_at=NOW + timedelta(minutes=5),
            )
        result = self.load()
        self.assertEqual(4, len(result))
        self.assertTrue((result.playoff_probability == 0.63).all())
        self.assertTrue((result.source == "kalshi").all())

    def test_other_sources_cannot_supply_qualification(self):
        for source in ("powerrankingsguru", "playoffstatus", "espn_fpi", "moneypuck", "polymarket"):
            self.add(team_abbreviation=source, source=source)
        self.assertTrue(self.load().playoff_probability.isna().all())

    def test_playoffstatus_model_rows_do_not_become_historical_or_current_analysis_evidence(self):
        self.add(sport="nhl", team_abbreviation="PHI", source="playoffstatus", playoff_probability=0.51)
        self.add(
            sport="nhl",
            team_abbreviation="PIT",
            source="playoffstatus",
            playoff_probability=0.72,
        )
        games = pd.DataFrame(
            [
                game(
                    sport="nhl",
                    home_team_abbreviation="PHI",
                    away_team_abbreviation="PIT",
                    season_year=2027,
                    scheduled_start=NOW - timedelta(days=1),
                )
            ]
        )
        rows = load_team_ratings_for_games(self.engine, games, evaluated_at=NOW)
        self.assertTrue(rows.playoff_probability.isna().all())
        result = calculate_hype_scores_batch(
            games, pd.DataFrame([odds()]), rows, evaluated_at=NOW
        )
        self.assertTrue(pd.isna(result.iloc[0].home_playoff_odds))
        self.assertEqual(50, result.iloc[0].stakes_score)

    def test_polymarket_final_cannot_override_or_fill_missing_nhl_qualification(self):
        self.add(sport="nhl", team_abbreviation="BOS", fetched_at=NOW - timedelta(minutes=1))
        self.add(
            sport="nhl",
            team_abbreviation="TOR",
            playoff_probability=None,
            fetched_at=NOW - timedelta(minutes=1),
        )
        for team in ("BOS", "TOR"):
            self.add(
                sport="nhl",
                team_abbreviation=team,
                source="polymarket",
                playoff_probability=1.0,
                playoff_probability_is_final=True,
                fetched_at=NOW,
            )
        rows = self.load(sport="nhl").set_index("team_abbreviation")
        self.assertEqual(0.63, rows.loc["BOS", "playoff_probability"])
        self.assertTrue(pd.isna(rows.loc["TOR", "playoff_probability"]))
        self.assertTrue((rows.source == "kalshi").all())
        self.assertFalse(rows.playoff_probability_is_final.any())

    def test_newest_null_blocks_older_quote_and_other_provider(self):
        self.add(fetched_at=NOW - timedelta(minutes=5))
        self.add(playoff_probability=None)
        self.add(source="playoffstatus", playoff_probability=0.99)
        row = self.load().iloc[0]
        self.assertTrue(pd.isna(row.playoff_probability))
        self.assertEqual(pd.Timestamp(NOW), row.fetched_at)

    def test_newest_wrong_source_season_and_future_timestamp_block_fallback(self):
        for index, invalid in enumerate(
            (
                {"source_season": 2025},
                {"source_season": None},
                {"fetched_at": NOW + timedelta(seconds=1)},
                {"fetched_at": None},
            )
        ):
            self.add(team_abbreviation=str(index), fetched_at=NOW - timedelta(minutes=5))
            self.add(team_abbreviation=str(index), **invalid)
        self.assertTrue(self.load().playoff_probability.isna().all())

    def test_freshness_boundary(self):
        self.add(team_abbreviation="fresh", fetched_at=NOW - timedelta(minutes=59, seconds=59))
        self.add(team_abbreviation="expired", fetched_at=NOW - timedelta(hours=1))
        self.add(team_abbreviation="old", fetched_at=NOW - timedelta(hours=2))
        rows = self.load().set_index("team_abbreviation")
        self.assertEqual(0.63, rows.loc["fresh", "playoff_probability"])
        self.assertTrue(pd.isna(rows.loc["expired", "playoff_probability"]))
        self.assertTrue(pd.isna(rows.loc["old", "playoff_probability"]))

    def test_only_final_endpoints_do_not_expire(self):
        for team, value, final in (
            ("yes", 1.0, True),
            ("no", 0.0, True),
            ("invalid", 0.6, True),
            ("not_final", 1.0, False),
        ):
            self.add(
                team_abbreviation=team,
                playoff_probability=value,
                playoff_probability_is_final=final,
                fetched_at=NOW - timedelta(days=400),
            )
        rows = self.load().set_index("team_abbreviation")
        self.assertEqual(1.0, rows.loc["yes", "playoff_probability"])
        self.assertEqual(0.0, rows.loc["no", "playoff_probability"])
        self.assertTrue(rows.loc["yes", "playoff_probability_is_final"])
        self.assertTrue(pd.isna(rows.loc["invalid", "playoff_probability"]))
        self.assertFalse(rows.loc["invalid", "playoff_probability_is_final"])
        self.assertTrue(pd.isna(rows.loc["not_final", "playoff_probability"]))

    def test_final_still_requires_season_value_and_nonfuture_fetch(self):
        for invalid in (
            {"source_season": 2025},
            {"fetched_at": NOW + timedelta(seconds=1)},
            {"playoff_probability": None},
            {"playoff_probability": float("inf")},
            {"playoff_probability": float("nan")},
            {"playoff_probability": -0.1},
            {"playoff_probability": 1.1},
        ):
            with self.subTest(invalid=invalid):
                row = rating(playoff_probability=1.0, playoff_probability_is_final=True)
                row.update(invalid)
                self.assertIsNone(
                    playoff_probability(row, sport="nfl", season=2026, evaluated_at=NOW)
                )

    def test_nonfinal_value_validation_and_unsupported_sport(self):
        for value in (float("inf"), float("-inf"), float("nan"), -0.1, 1.1, None):
            with self.subTest(value=value):
                self.assertIsNone(
                    playoff_probability(
                        rating(playoff_probability=value),
                        sport="nfl",
                        season=2026,
                        evaluated_at=NOW,
                    )
                )
        self.assertIsNone(
            playoff_probability(
                rating(sport="world_cup"), sport="world_cup", season=2026, evaluated_at=NOW
            )
        )
        for value in (0.0, 1.0):
            self.assertEqual(
                value,
                playoff_probability(
                    rating(playoff_probability=value), sport="nfl", season=2026, evaluated_at=NOW
                ),
            )

    def test_championship_and_strength_survive_missing_qualification(self):
        self.add(playoff_probability=None)
        self.add(
            source="powerrankingsguru",
            playoff_probability=0.99,
            championship_probability=0.2,
            fetched_at=NOW - timedelta(days=3),
        )
        self.add(
            source="espn_fpi",
            playoff_probability=None,
            fpi_rating=8.0,
            fetched_at=NOW - timedelta(days=2),
        )
        self.add(
            source="playoffstatus",
            playoff_probability=None,
            championship_probability=0.3,
            fetched_at=NOW - timedelta(days=1),
        )
        self.add(
            team_abbreviation="strength-only",
            source="espn_fpi",
            playoff_probability=None,
            fpi_rating=3.0,
        )
        row = self.load().set_index("team_abbreviation").loc["KC"]
        self.assertTrue(pd.isna(row.playoff_probability))
        self.assertEqual(8.0, row.fpi_rating)
        self.assertEqual(0.3, row.championship_probability)
        self.assertEqual("playoffstatus", row.championship_source)
        self.assertIn("strength-only", self.load().team_abbreviation.tolist())

    def test_explicit_seasons_never_merge(self):
        self.add(
            season=2025,
            source_season=2025,
            playoff_probability=0.2,
            fpi_rating=2.0,
            fetched_at=NOW + timedelta(minutes=1),
        )
        self.add(season=2026, source_season=2026, playoff_probability=0.8, fpi_rating=8.0)
        rows = self.load().set_index("season")
        self.assertEqual(2, len(rows))
        self.assertEqual(8.0, rows.loc[2026, "fpi_rating"])
        self.assertEqual(0.8, rows.loc[2026, "playoff_probability"])
        self.assertEqual([2025], self.load(season=2025).season.tolist())

    def test_loader_uses_bound_parameters_and_does_not_read_secrets(self):
        self.add()
        with patch(
            "hype_score.database._load_env_file", side_effect=AssertionError("secret access")
        ):
            self.assertTrue(self.load(sport="nfl' OR 1=1 --", season=2026).empty)
            self.assertEqual(1, len(self.load(sport="nfl", season=2026)))

    def test_game_loader_preserves_explicit_seasons_and_binds_limit(self):
        rows = pd.DataFrame([game(id="old", season_year=2025), game(id="new")]).rename(
            columns={
                "home_team_abbreviation": "home_team_abbr",
                "away_team_abbreviation": "away_team_abbr",
            }
        )
        rows["actual_start"] = None
        rows["end_time"] = None
        rows.to_sql("games", self.engine, index=False)
        loaded = load_games(self.engine, sport="nfl", limit=2)
        self.assertEqual({2025, 2026}, set(loaded.season_year))
        self.assertEqual({2}, set(loaded.season_type))
        self.assertTrue(load_games(self.engine, limit=0).empty)
        self.assertTrue(load_games(self.engine, sport="nfl' OR 1=1 --").empty)

    def test_requested_teams_without_ratings_and_empty_games_are_preserved(self):
        self.add()
        games = pd.DataFrame([game(), game(id="unknown", home_team_abbreviation="NO-DATA")])
        rows = load_team_ratings_for_games(self.engine, games, evaluated_at=NOW)
        self.assertEqual({"KC", "BUF", "NO-DATA"}, set(rows.team_abbreviation))
        self.assertTrue(rows[rows.team_abbreviation != "KC"].playoff_probability.isna().all())
        self.assertEqual(
            RATING_COLUMNS,
            list(
                load_team_ratings_for_games(self.engine, pd.DataFrame(), evaluated_at=NOW).columns
            ),
        )

    def test_empty_ratings_database_keeps_both_teams_and_the_game(self):
        games = pd.DataFrame([game()])
        rows = load_team_ratings_for_games(self.engine, games, evaluated_at=NOW)
        self.assertEqual({"KC", "BUF"}, set(rows.team_abbreviation))
        self.assertTrue(rows.playoff_probability.isna().all())
        result = calculate_hype_scores_batch(games, pd.DataFrame([odds()]), rows, evaluated_at=NOW)
        self.assertEqual(1, len(result))
        self.assertTrue(pd.isna(result.iloc[0].home_playoff_odds))
        self.assertTrue(pd.isna(result.iloc[0].away_playoff_odds))
        self.assertEqual(50, result.iloc[0].stakes_score)

    def test_loader_and_batch_agree_and_evaluation_time_is_rechecked(self):
        self.add()
        self.add(team_abbreviation="BUF", playoff_probability=0.73)
        games = pd.DataFrame([game()])
        rows = load_team_ratings_for_games(self.engine, games, evaluated_at=NOW)
        result = calculate_hype_scores_batch(games, pd.DataFrame([odds()]), rows, evaluated_at=NOW)
        self.assertEqual(0.63, result.iloc[0].home_playoff_odds)
        self.assertEqual(0.73, result.iloc[0].away_playoff_odds)
        expired = calculate_hype_scores_batch(
            games, pd.DataFrame([odds()]), rows, evaluated_at=NOW + timedelta(hours=1)
        )
        self.assertTrue(pd.isna(expired.iloc[0].home_playoff_odds))
        self.assertEqual(50, expired.iloc[0].stakes_score)


class ScoringConsumerTests(unittest.TestCase):
    def score(self, home=None, away=None, **game_changes):
        return calculate_hype_score(
            Game(**game(**game_changes)), PregameOdds(**odds()), home, away, evaluated_at=NOW
        )

    def test_unknown_probability_is_null_and_stakes_neutral_for_each_league(self):
        for sport in ("nfl", "nba", "nhl", "mlb"):
            with self.subTest(sport=sport):
                home = TeamRating(**rating(sport=sport, playoff_probability=None))
                away = TeamRating(
                    **rating(sport=sport, team_abbreviation="BUF", playoff_probability=1.0)
                )
                result = self.score(home, away, sport=sport)
                self.assertIsNone(result.components.home_playoff_odds)
                self.assertEqual(1.0, result.components.away_playoff_odds)
                self.assertEqual(50.0, result.components.stakes_score)
                weights = HypeConfig.default().get_sport_weights(sport)
                self.assertAlmostEqual(
                    50 * weights.stakes_weight
                    + 50 * weights.quality_weight
                    + 50 * weights.closeness_weight,
                    result.hype_score,
                )

    def test_direct_scoring_and_stakes_reject_other_providers(self):
        home = TeamRating(**rating(source="powerrankingsguru", fpi_rating=8))
        away = TeamRating(**rating(source="playoffstatus", team_abbreviation="BUF", fpi_rating=6))
        result = self.score(home, away)
        self.assertIsNone(result.components.home_playoff_odds)
        self.assertEqual(8, result.components.home_team_rating)
        self.assertEqual(50, result.components.stakes_score)
        self.assertEqual(
            50,
            calculate_stakes(
                NOW + timedelta(hours=2),
                "nfl",
                home,
                away,
                HypeConfig.default(),
                season=2026,
                evaluated_at=NOW,
            ),
        )

    def test_past_or_started_games_cannot_use_current_or_final_snapshots(self):
        for final, probability in ((False, 0.6), (True, 1.0), (True, 0.0)):
            for changes in (
                {"scheduled_start": NOW - timedelta(days=100)},
                {"scheduled_start": NOW},
                {"status": "final"},
                {"status": "in_progress"},
            ):
                with self.subTest(final=final, changes=changes):
                    home = TeamRating(
                        **rating(
                            playoff_probability=probability, playoff_probability_is_final=final
                        )
                    )
                    away = TeamRating(**rating(team_abbreviation="BUF"))
                    result = self.score(home, away, **changes)
                    self.assertIsNone(result.components.home_playoff_odds)
                    self.assertIsNone(result.components.away_playoff_odds)
                    self.assertEqual(50, result.components.stakes_score)

    def test_postseason_has_maximum_stakes_without_claiming_historical_evidence(self):
        for sport in ("nfl", "nba", "nhl", "mlb"):
            for status, start in (
                ("scheduled", NOW + timedelta(hours=2)),
                ("final", NOW - timedelta(days=100)),
            ):
                with self.subTest(sport=sport, status=status):
                    home = TeamRating(
                        **rating(
                            sport=sport,
                            playoff_probability=1.0,
                            playoff_probability_is_final=True,
                        )
                    )
                    result = self.score(
                        home,
                        None,
                        sport=sport,
                        season_type=3,
                        status=status,
                        scheduled_start=start,
                    )
                    self.assertEqual(100.0, result.components.stakes_score)
                    self.assertIsNone(result.components.away_playoff_odds)
                    if status == "final":
                        self.assertIsNone(result.components.home_playoff_odds)
        no_ratings = self.score(season_type=3)
        self.assertEqual(100.0, no_ratings.components.stakes_score)
        self.assertIsNone(no_ratings.components.home_playoff_odds)

    def test_batch_preserves_historical_postseason_stakes(self):
        games = pd.DataFrame(
            [game(status="final", season_type=3, scheduled_start=NOW - timedelta(days=100))]
        )
        result = calculate_hype_scores_batch(
            games,
            pd.DataFrame([odds()]),
            pd.DataFrame([rating(playoff_probability=1.0, playoff_probability_is_final=True)]),
            evaluated_at=NOW,
        )
        self.assertEqual(100.0, result.iloc[0].stakes_score)
        self.assertTrue(pd.isna(result.iloc[0].home_playoff_odds))
        self.assertTrue(pd.isna(result.iloc[0].away_playoff_odds))

    def test_old_final_probability_remains_valid_for_a_future_game(self):
        home = TeamRating(
            **rating(
                playoff_probability=1.0,
                playoff_probability_is_final=True,
                fetched_at=NOW - timedelta(days=400),
            )
        )
        away = TeamRating(
            **rating(
                team_abbreviation="BUF",
                playoff_probability=0.0,
                playoff_probability_is_final=True,
                fetched_at=NOW - timedelta(days=400),
            )
        )
        result = self.score(home, away)
        self.assertEqual(1.0, result.components.home_playoff_odds)
        self.assertEqual(0.0, result.components.away_playoff_odds)

    def test_setting_evaluation_to_past_does_not_turn_current_fetch_into_evidence(self):
        result = calculate_hype_score(
            Game(**game(scheduled_start=NOW - timedelta(days=1))),
            PregameOdds(**odds()),
            TeamRating(**rating(playoff_probability=1.0, playoff_probability_is_final=True)),
            TeamRating(**rating(team_abbreviation="BUF")),
            evaluated_at=NOW - timedelta(days=2),
        )
        self.assertIsNone(result.components.home_playoff_odds)

    def test_season_and_team_mismatch_remove_rating_without_cross_season_fallback(self):
        for changes in ({"season": 2025}, {"team_abbreviation": "OTHER"}, {"sport": "nba"}):
            result = self.score(TeamRating(**rating(fpi_rating=8, **changes)))
            self.assertIsNone(result.components.home_team_rating)
            self.assertIsNone(result.components.home_playoff_odds)
        self.assertIsNone(
            self.score(TeamRating(**rating()), season_year=None).components.home_playoff_odds
        )

    def test_batch_keys_include_season_and_games_without_ratings_remain(self):
        games = pd.DataFrame(
            [
                game(id="old", season_year=2025),
                game(id="new"),
                game(id="missing", season_year=2024),
                game(id="no-season", season_year=None),
            ]
        )
        rows = pd.DataFrame(
            [
                rating(season=2025, source_season=2025, playoff_probability=0.2),
                rating(playoff_probability=0.8),
            ]
        )
        result = calculate_hype_scores_batch(
            games, pd.DataFrame([odds(key) for key in games.id]), rows, evaluated_at=NOW
        ).set_index("game_id")
        self.assertEqual(4, len(result))
        self.assertEqual(0.2, result.loc["old", "home_playoff_odds"])
        self.assertEqual(0.8, result.loc["new", "home_playoff_odds"])
        self.assertTrue(pd.isna(result.loc["missing", "home_playoff_odds"]))
        self.assertTrue(pd.isna(result.loc["no-season", "home_playoff_odds"]))

    def test_batch_rejects_unselected_duplicate_keys(self):
        with self.assertRaisesRegex(ValueError, "Duplicate team/season"):
            calculate_hype_scores_batch(
                pd.DataFrame([game()]),
                pd.DataFrame([odds()]),
                pd.DataFrame([rating(), rating(source="playoffstatus")]),
                evaluated_at=NOW,
            )

    def test_time_must_be_explicit_and_timezone_aware(self):
        with self.assertRaisesRegex(ValueError, "timezone-aware"):
            self.score_at(NOW.replace(tzinfo=None))
        with self.assertRaises(TypeError):
            calculate_hype_score(Game(**game()), PregameOdds(**odds()), None, None)

    def score_at(self, now):
        return calculate_hype_score(
            Game(**game()), PregameOdds(**odds()), None, None, evaluated_at=now
        )


class NotebookConsumerTests(unittest.TestCase):
    def test_notebook_calls_use_explicit_evaluation_and_season_aware_loader(self):
        for path in NOTEBOOKS.glob("*.ipynb"):
            notebook = json.loads(path.read_text())
            for cell in notebook["cells"]:
                if cell["cell_type"] != "code":
                    continue
                source = "".join(cell["source"])
                tree = ast.parse(source)
                for node in ast.walk(tree):
                    if not isinstance(node, ast.Call) or not isinstance(node.func, ast.Name):
                        continue
                    name = node.func.id
                    self.assertNotEqual("load_team_ratings", name, str(path))
                    if name in (
                        "load_team_ratings_for_games",
                        "calculate_hype_score",
                        "calculate_hype_scores_batch",
                    ):
                        self.assertIn("evaluated_at", {kw.arg for kw in node.keywords}, str(path))
                if not path.name.startswith("03_"):
                    self.assertEqual([], cell["outputs"], str(path))

    def test_synthetic_regression_notebook_uses_new_policy(self):
        notebook = json.loads((NOTEBOOKS / "06_regression_tests.ipynb").read_text())
        namespace = {}
        with contextlib.redirect_stdout(io.StringIO()):
            for cell in notebook["cells"]:
                if cell["cell_type"] == "code":
                    exec(
                        compile("".join(cell["source"]), "synthetic-regression-notebook", "exec"),
                        namespace,
                    )
        self.assertTrue(namespace["all_passed"])


if __name__ == "__main__":
    unittest.main()
