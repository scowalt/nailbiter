import csv
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


def estimated_swing_stakes(probability, remaining):
    import math
    low, high = -40.0, 40.0
    for _ in range(200):
        middle = (low + high) / 2
        if math.erfc(-middle / math.sqrt(2)) / 2 < probability:
            low = middle
        else:
            high = middle
    z = (low + high) / 2
    return 80 * math.exp(-z * z / 2) / math.sqrt(2 * math.pi) / math.sqrt(.55 ** 2 * (remaining - 1) + .7 ** 2)


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
        percent = lambda n: Fraction(n, 100)
        away_qualification = 80 * ((1 - percent(18)) - (1 - percent(43)))
        home_qualification = 80 * ((1 - percent(26)) - (1 - percent(46)))
        self.assertEqual(away_qualification, 20)
        self.assertEqual(home_qualification, 16)
        below_one_percent_upper = 1
        away_seeding = 80 * (Fraction(1, 2) * (percent(9 + 13 + 14 + 16) - percent(below_one_percent_upper + 5 + 8 + 10))
                             + Fraction(1, 2) * (percent(9) - percent(below_one_percent_upper)))
        home_seeding = 80 * (Fraction(1, 2) * (percent(12 + 15 + 14 + 13) - percent(4 + 7 + 9 + 10)) + Fraction(1, 2) * (percent(12) - percent(4)))
        away, home = away_qualification + away_seeding, home_qualification + home_seeding
        self.assertEqual(away, Fraction(172, 5))
        self.assertEqual(home, Fraction(144, 5))
        rank_one, rank_two = Fraction(100 * 63, 64), Fraction(100 * 61, 64)
        quality = 2 * rank_one * rank_two / (rank_one + rank_two)
        total = Fraction(45, 100) * max(home, away) + Fraction(35, 100) * quality + Fraction(20, 100) * 80 * quality / 100
        self.assertEqual(total, Fraction(6435441, 99200))
        self.assertEqual(round(total), 65)

    def test_nhl_seeding_controls_are_complete_and_have_an_independent_page_oracle(self):
        from collections import Counter
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
        stakes = estimated_swing_stakes(.9, 84)
        total = .35 * stakes + .35 * float(quality) + 30
        self.assertAlmostEqual(stakes, 2.775007774069287, places=12)
        self.assertAlmostEqual(total, 54.99884580603063, places=12)

    def test_nba_seeding_controls_leave_unpublished_games_unassigned_and_have_an_independent_oracle(self):
        from collections import Counter
        from fractions import Fraction
        rows = list(csv.DictReader(io.StringIO((ROOT / 'apps/backend.tests/Fixtures/ProHype/nba-seeding-contests.csv').read_text())))
        self.assertEqual(len(rows), 1200)
        self.assertEqual(len({row['id'] for row in rows}), 1200)
        counts = Counter(team for row in rows for team in [row['home'], row['away']])
        self.assertEqual(len(counts), 30)
        self.assertEqual(set(counts.values()), {80})
        target = [row for row in rows if row['home'] == 'BOS' and row['away'] == 'MIA']
        self.assertEqual(len(target), 2)
        strength_four, strength_nine = Fraction(265, 3), Fraction(215, 3)
        quality = 2 * strength_four * strength_nine / (strength_four + strength_nine)
        self.assertEqual(quality, Fraction(11395, 144))
        closeness = 100 * (1 - abs(2 * Fraction(3, 5) - 1))
        self.assertEqual(closeness, 80)
        stakes = estimated_swing_stakes(.9, 74)
        total = .3 * stakes + .4 * float(quality) + .3 * float(closeness)
        self.assertAlmostEqual(stakes, 2.955106581196874, places=12)
        self.assertAlmostEqual(total, 56.53930975213684, places=12)

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

    def test_mlb_scoreboard_observations_cover_every_authored_baseball_case(self):
        from datetime import datetime
        from zoneinfo import ZoneInfo
        eastern = lambda value: datetime.fromisoformat(value).astimezone(ZoneInfo('America/New_York')).strftime('%Y%m%d')
        prefix = ROOT / 'apps/backend.tests/Fixtures/MlbScoreboard'
        fixture = json.loads((prefix / 'espn-basic.json').read_text())
        self.assertTrue(fixture['synthetic'])
        self.assertIn('no provider responses', fixture['provenance'])
        self.assertIn('not current provider conformance, field availability, authorization or live latency', fixture['evidenceLimit'])
        contests = fixture['contests']
        self.assertEqual({c['case'] for c in contests.values()}, {
            'final-unplayed-bottom-ninth', 'final-extra-innings', 'doubleheader-game-1', 'doubleheader-game-2', 'postponed',
            'makeup', 'canceled', 'rain-delay', 'suspended-then-resumed'})
        self.assertTrue(all(identity.startswith('9913001') and c['competitionId'] == identity for identity, c in contests.items()))
        self.assertEqual(len({c['home']['id'] for c in contests.values()} | {c['away']['id'] for c in contests.values()}), 14)
        for contest in contests.values():
            self.assertNotEqual(contest['home']['id'], contest['away']['id'])
        first, second = contests['991300103'], contests['991300104']
        self.assertEqual((first['doubleheader'], second['doubleheader']), ({'game': 1, 'pairedWith': '991300104'}, {'game': 2, 'pairedWith': '991300103'}))
        self.assertEqual((first['home'], first['away'], eastern(first['start'])), (second['home'], second['away'], eastern(second['start'])))
        self.assertEqual(contests['991300106']['makeupOf'], '991300105')
        self.assertEqual((contests['991300106']['home'], contests['991300106']['away']), (contests['991300105']['home'], contests['991300105']['away']))
        self.assertEqual(contests['991300102']['listed'], ['away', 'home'])

        observations = {o['id']: o for o in fixture['observations']}
        self.assertEqual(list(observations), ['opening', 'advance', 'partial', 'out-of-order', 'future', 'resumption'])
        served = [datetime.fromisoformat(o['servedAt']) for o in observations.values()]
        self.assertEqual(served, sorted(served))
        moment = {key: datetime.fromisoformat(o['responseDate']) for key, o in observations.items()}
        self.assertLess(moment['out-of-order'], moment['advance'])
        self.assertGreater(moment['future'], datetime.fromisoformat(observations['future']['servedAt']))
        expected = {key: {identity: state for board in o['boards'] for identity, state in board['expected'].items()} for key, o in observations.items()}
        dates = {key: {identity: board['date'] for board in o['boards'] for identity in board['expected']} for key, o in observations.items()}
        self.assertEqual(expected['out-of-order'], expected['opening'])
        self.assertEqual((expected['opening']['991300104']['detail'], expected['advance']['991300104']['detail'], expected['future']['991300104']['detail']),
                         ('Top 5th', 'Bottom 6th', 'Top 7th'))
        self.assertEqual((dates['opening']['991300109'], dates['resumption']['991300109']), ('20270710', '20270711'))
        self.assertEqual((expected['opening']['991300109']['status'], expected['resumption']['991300109']['status']), ('suspended', 'final'))
        resumed = expected['resumption']['991300109']
        for side in ['away', 'home']:
            suspended = expected['opening']['991300109'][side]['innings']
            self.assertEqual(resumed[side]['innings'][:len(suspended)], suspended)
        self.assertEqual({expected['resumption'][i]['status'] for i in ['991300105', '991300106', '991300107']}, {'postponed', 'final', 'canceled'})

        statuses = {'final': ('STATUS_FINAL', 'post', True), 'postponed': ('STATUS_POSTPONED', 'post', False), 'canceled': ('STATUS_CANCELED', 'post', False),
                    'delayed': ('STATUS_RAIN_DELAY', 'in', False), 'suspended': ('STATUS_SUSPENDED', 'post', False),
                    'in': ('STATUS_IN_PROGRESS', 'in', False), 'scheduled': ('STATUS_SCHEDULED', 'pre', False)}
        self.assertEqual({state['status'] for states in expected.values() for state in states.values()}, set(statuses))
        for key, observation in observations.items():
            for board in observation['boards']:
                self.assertEqual(board['url'], f'https://site.web.api.espn.com/apis/site/v2/sports/baseball/mlb/scoreboard?dates={board["date"]}')
                events = {event['id']: event for event in board['body']['events']}
                self.assertEqual(set(events), set(board['expected']))
                for identity, state in board['expected'].items():
                    resumed_later = (key, identity) == ('resumption', '991300109')
                    self.assertEqual(board['date'], '20270711' if resumed_later else eastern(contests[identity]['start']), (key, identity))
                    self.assertEqual((state['name'], state['state'], state['completed']), statuses[state['status']])
                    competition, = events[identity]['competitions']
                    self.assertEqual(competition['id'], identity)
                    self.assertEqual([c['homeAway'] for c in competition['competitors']], contests[identity]['listed'])
                    for side in ['home', 'away']:
                        line = state[side]
                        competitor, = [c for c in competition['competitors'] if c['homeAway'] == side]
                        self.assertEqual(competitor['team']['id'], contests[identity][side]['id'])
                        self.assertEqual(competitor['score'], str(line['runs']))
                        for count in ['hits', 'errors']:
                            self.assertEqual(competitor.get(count), line[count])
                            self.assertEqual(count in competitor, line[count] is not None)
                        self.assertEqual('linescores' in competitor, line['innings'] is not None)
                        if line['innings'] is not None:
                            self.assertEqual(sum(line['innings']), line['runs'], (key, identity, side))
                            self.assertEqual([i['period'] for i in competitor['linescores']], list(range(1, len(line['innings']) + 1)))

        opening = expected['opening']
        self.assertEqual((len(opening['991300101']['away']['innings']), len(opening['991300101']['home']['innings'])), (9, 8))
        self.assertEqual(opening['991300101']['away']['innings'][8], 0)
        self.assertEqual(opening['991300101']['home']['errors'], 0)
        self.assertEqual((opening['991300102']['period'], len(opening['991300102']['away']['innings'])), (11, 11))
        self.assertEqual(expected['resumption']['991300106']['home']['innings'], [0] * 9)
        for identity in ['991300105', '991300107']:
            self.assertEqual(opening[identity]['home'], {'innings': None, 'runs': 0, 'hits': None, 'errors': None})
        partial = expected['partial']['991300104']
        self.assertEqual((partial['away']['hits'], partial['away']['errors'], partial['home']['innings']), (None, 0, None))

    def test_mlb_line_scores_follow_half_inning_order_in_every_observation(self):
        import re
        fixture = json.loads((ROOT / 'apps/backend.tests/Fixtures/MlbScoreboard/espn-basic.json').read_text())
        checked = 0
        for observation in fixture['observations']:
            for board in observation['boards']:
                for identity, state in board['expected'].items():
                    away, home = state['away']['innings'], state['home']['innings']
                    if away is None or home is None:
                        continue
                    where = (observation['id'], identity)
                    self.assertIn(len(away) - len(home), (0, 1), where)
                    self.assertEqual(len(away), state['period'], where)
                    half = re.fullmatch(r'(Top|Bottom) (\d+)(?:st|nd|rd|th)', state['detail'])
                    if half:
                        inning = int(half[2])
                        self.assertEqual((len(away), len(home)), (inning, inning - 1 if half[1] == 'Top' else inning), where)
                    if state['status'] == 'final':
                        self.assertGreaterEqual(len(away), 9, where)
                        if len(home) < len(away):
                            self.assertGreater(sum(home), sum(away), where)
                    checked += 1
        self.assertEqual(checked, 36)

    def test_mlb_rich_frames_are_normalized_inputs_with_nulls_distinct_from_zero(self):
        prefix = ROOT / 'apps/backend.tests/Fixtures/MlbScoreboard'
        contests = json.loads((prefix / 'espn-basic.json').read_text())['contests']
        fixture = json.loads((prefix / 'rich-frames.json').read_text())
        self.assertTrue(fixture['synthetic'] and fixture['normalized'])
        self.assertIn('not an ESPN situation schema', fixture['provenance'])
        frames = {frame['id']: frame for frame in fixture['frames']}
        self.assertEqual(len(frames), len(fixture['frames']))
        self.assertTrue(all(frame['gameId'] in contests for frame in frames.values()))
        self.assertEqual(set(frames), {
            'active-complete', 'zero-count-empty-bases', 'partial-optional', 'runner-unknown', 'rich-unavailable', 'out-of-order', 'future',
            'retained-after-final', 'retained-while-delayed', 'retained-while-suspended'})
        zero = frames['zero-count-empty-bases']
        self.assertEqual(zero['count'], {'balls': 0, 'strikes': 0, 'outs': 0})
        self.assertTrue(all(base == {'occupied': False, 'runner': None} for base in zero['bases'].values()))
        self.assertIsNone(frames['partial-optional']['bases'])
        self.assertIsNone(frames['partial-optional']['matchup']['pitcher'])
        self.assertEqual(frames['runner-unknown']['bases']['first'], {'occupied': True, 'runner': None})
        unavailable = frames['rich-unavailable']
        self.assertEqual([unavailable[key] for key in ['half', 'count', 'bases', 'matchup']], [None] * 4)
        self.assertLess(frames['out-of-order']['sourceRevisionAt'], frames['zero-count-empty-bases']['sourceRevisionAt'])
        self.assertGreater(frames['future']['observedAt'], frames['future']['receivedAt'])
        self.assertEqual({contests[frames[f'retained-{case}']['gameId']]['case'] for case in ['after-final', 'while-delayed', 'while-suspended']},
                         {'final-unplayed-bottom-ninth', 'rain-delay', 'suspended-then-resumed'})


if __name__ == '__main__':
    unittest.main()
