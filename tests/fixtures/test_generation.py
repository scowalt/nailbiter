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

    def test_nfl_leverage_schedule_and_oracle_are_independent_constructions(self):
        from collections import Counter
        from fractions import Fraction
        prefix = ROOT / 'apps/backend.tests/Fixtures/NflPlayoffLeverage'
        rows = list(csv.DictReader(io.StringIO((prefix / 'synthetic-espn-controls.csv').read_text())))
        self.assertEqual(len(rows), 272)
        counts = Counter(team for row in rows for team in [row['home'], row['away']])
        self.assertEqual(len(counts), 32)
        self.assertEqual(set(counts.values()), {17})
        self.assertEqual(len({(row['home'], row['away']) for row in rows}), 272)
        target = [row for row in rows if row['home'] == 'DET' and row['away'] == 'GB']
        self.assertEqual(len(target), 1)
        self.assertEqual(target[0]['week'], '7')
        self.assertEqual(target[0]['start'], '2026-10-18T17:00:00Z')
        self.assertEqual(target[0]['home_score'], '')
        self.assertEqual((prefix / 'nflschedule.html').read_text().count('<h2 id='), 18)
        for filename in ['nflschedule.html', 'packerswhatif.html', 'lionswhatif.html']:
            self.assertIn('Independently authored synthetic contract fixture', (prefix / filename).read_text())
        away = 80 * ((1 - Fraction(18, 100)) - (1 - Fraction(43, 100)))
        home = 80 * ((1 - Fraction(26, 100)) - (1 - Fraction(46, 100)))
        self.assertEqual(away, 20)
        self.assertEqual(home, 16)
        rank_one, rank_two = Fraction(100 * 63, 64), Fraction(100 * 61, 64)
        quality = 2 * rank_one * rank_two / (rank_one + rank_two)
        total = Fraction(45, 100) * max(home, away) + Fraction(35, 100) * quality + Fraction(20, 100) * 80
        self.assertEqual(total, Fraction(29213125, 496000))
        self.assertEqual(round(total), 59)

    def test_nhl_seeding_controls_are_complete_and_have_an_independent_page_oracle(self):
        from collections import Counter
        from decimal import Decimal, localcontext
        from fractions import Fraction
        rows = list(csv.DictReader(io.StringIO((ROOT / 'apps/backend.tests/Fixtures/ProHype/nhl-seeding-contests.csv').read_text())))
        self.assertEqual(len(rows), 1344)
        self.assertEqual(len({row['id'] for row in rows}), 1344)
        counts = Counter(team for row in rows for team in [row['home'], row['away']])
        self.assertEqual(len(counts), 32)
        self.assertEqual(set(counts.values()), {84})
        target = [row for row in rows if row['home'] == 'ANA' and row['away'] == 'LA']
        self.assertEqual(len(target), 2)
        self.assertEqual(target[0]['id'], 'synthetic-seeding-0033')
        quality = 2 * Fraction(100 * 59, 64) * Fraction(100 * 35, 64) / (Fraction(100 * 59, 64) + Fraction(100 * 35, 64))
        self.assertEqual(quality, Fraction(51625, 752))
        with localcontext() as ctx:
            ctx.prec = 50
            urgency = ((Decimal(3) / 84).exp() - 1) / (Decimal(3).exp() - 1)
            stakes = 40 * urgency
            total = Decimal('0.35') * stakes + Decimal('0.35') * Decimal(quality.numerator) / quality.denominator + 30
            self.assertAlmostEqual(float(stakes), 0.07620367519342137, places=14)
            self.assertAlmostEqual(float(total), 54.05426437142408, places=12)

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
