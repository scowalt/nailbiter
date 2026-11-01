import csv
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class SyntheticFixtureTests(unittest.TestCase):
    def test_generator_is_offline_and_reproducible(self):
        subprocess.run(['python3', str(ROOT / 'scripts/test-fixtures/generate.py'), '--check'], check=True)
        source = (ROOT / 'scripts/test-fixtures/generate.py').read_text()
        for forbidden in ['urlopen', 'requests.', 'http.client', 'read_text(', 'read_bytes(']:
            if forbidden.startswith('read_'):
                continue
            self.assertNotIn(forbidden, source)
        manifest = json.loads((ROOT / 'tests/fixtures/manifest.json').read_text())
        self.assertIn('no provider responses', manifest['provenance'])
        for name, digest in manifest['sha256'].items():
            self.assertEqual(hashlib.sha256((ROOT / name).read_bytes()).hexdigest(), digest)
        actual = {str(path.relative_to(ROOT)) for path in ROOT.glob('apps/*tests/Fixtures/**/*')
                  if path.is_file() and path.suffix in {'.json', '.csv', '.txt', '.html'}}
        self.assertEqual(actual, set(manifest['sha256']))

    def test_rank_order_is_a_control_not_sorted_forecast_values(self):
        csv_text = (ROOT / 'apps/backend.tests/Fixtures/MoneyPuck/rankings_current.csv').read_text()
        rows = list(csv.DictReader(io.StringIO(csv_text)))
        self.assertEqual(len(rows), 32)
        self.assertEqual(len({row['teamCode'] for row in rows}), 32)
        self.assertEqual(rows[28]['teamCode'], 'CHI')
        self.assertEqual(rows[29]['teamCode'], 'SEA')
        self.assertLess(float(rows[28]['avg_overall_prediction']), float(rows[29]['avg_overall_prediction']))

    def test_quote_scenarios_require_normalization_and_preserve_distinct_outcomes(self):
        from fractions import Fraction
        nfl = json.loads((ROOT / 'apps/backend.tests/Fixtures/ProHype/nfl-pregame-401872985.json').read_text())
        markets = nfl['kalshi']['body']['markets']
        midpoints = [(Fraction(market['yes_bid_dollars']) + Fraction(market['yes_ask_dollars'])) / 2 for market in markets]
        self.assertEqual(sum(midpoints), Fraction(103, 100))
        self.assertEqual(midpoints[1] / sum(midpoints), Fraction(61, 103))
        mlb = json.loads((ROOT / 'apps/backend.tests/Fixtures/ProHype/mlb-pregame-401907973.json').read_text())
        lines = mlb['board']['body']['events'][0]['competitions'][0]['odds'][0]['moneyline']
        self.assertEqual(lines['home']['close']['odds'], '-120')
        self.assertEqual(lines['away']['close']['odds'], '+110')
        self.assertEqual(Fraction(6, 11) / (Fraction(6, 11) + Fraction(10, 21)), Fraction(63, 118))

    def test_chart_oracle_uses_exact_step_integrals_not_production_scorer(self):
        from fractions import Fraction
        csv_text = (ROOT / 'apps/backend.tests/Fixtures/Excitement/histories.csv').read_text()
        rows = list(csv.DictReader(io.StringIO(csv_text)))
        integral = lambda p: Fraction(4, 3) * p ** 3 if p <= Fraction(1, 2) else Fraction(1, 3) - Fraction(4, 3) * (1 - p) ** 3
        for identity, count, expected in [('990000001', 300, Fraction(2083333, 100000)), ('990000002', 320, Fraction(1404, 25))]:
            samples = [row for row in rows if row['game_id'] == identity]
            self.assertEqual(len(samples), count)
            probabilities = [Fraction(row['home_win_probability']) for row in samples]
            changes = [abs(right-left) for left, right in zip(probabilities, probabilities[1:]) if left != right]
            self.assertTrue(all(change >= Fraction(3, 100) for change in changes))
            score = 20 * sum(abs(integral(right) - integral(left)) for left, right in zip(probabilities, probabilities[1:]))
            self.assertEqual(score, expected)


if __name__ == '__main__':
    unittest.main()
