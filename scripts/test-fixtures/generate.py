import argparse
import copy
import csv
from datetime import datetime, timedelta, timezone
import hashlib
import io
import json
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[2]
OUTPUTS = {}
TEAMS = {
    'nhl': 'ANA BOS BUF CGY CAR CHI COL CBJ DAL DET EDM FLA LA MIN MTL NSH NJ NYI NYR OTT PHI PIT SJ SEA STL TB TOR UTAH VAN VGK WSH WPG'.split(),
    'nba': 'ATL BOS BKN CHA CHI CLE DAL DEN DET GS HOU IND LAC LAL MEM MIA MIL MIN NO NY OKC ORL PHI PHX POR SAC SA TOR UTAH WSH'.split(),
    'nfl': 'ARI ATL BAL BUF CAR CHI CIN CLE DAL DEN DET GB HOU IND JAX KC LAC LAR LV MIA MIN NE NO NYG NYJ PHI PIT SEA SF TB TEN WSH'.split(),
    'mlb': 'ARI ATH ATL BAL BOS CHC CHW CIN CLE COL DET HOU KC LAA LAD MIA MIL MIN NYM NYY PHI PIT SD SF SEA STL TB TEX TOR WSH'.split(),
}
IDS = {team: str(i + 101) for i, team in enumerate(TEAMS['nhl'])}
IDS.update({'ATL': '15', 'PHI': '22', 'PIT': '23', 'IND': '11', 'MIL': '8', 'LAD': '19', 'CHW': '4', 'CLE': '5'})
PATHS = {'nhl': 'hockey/nhl', 'nba': 'basketball/nba', 'nfl': 'football/nfl', 'mlb': 'baseball/mlb'}
SERIES = {'nhl': 'KXNHLPLAYOFF', 'nba': 'KXNBAPLAYOFF', 'nfl': 'KXNFLPLAYOFF', 'mlb': 'KXMLBPLAYOFFS'}
F = 'apps/backend.tests/Fixtures/'
FS = 'apps/backend.fsharp.tests/Fixtures/'


def emit(path, value):
    OUTPUTS[path] = (json.dumps(value, indent=2) + '\n') if not isinstance(value, str) else value


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def stamp(value):
    return value.isoformat(timespec='microseconds').replace('+00:00', '0Z')


def state(phase='pre', period=0):
    names = {'pre': 'STATUS_SCHEDULED', 'post': 'STATUS_FINAL', 'in': 'STATUS_IN_PROGRESS'}
    return {'type': {'name': names[phase], 'state': phase, 'completed': phase == 'post'}, 'period': period, 'displayClock': '0:00'}


def event(identity, home, away, date, year=2027, phase=2, status='pre', period=0, score=(0, 0), label=None, wins=None, best=7):
    competitors = []
    for i, (team, role) in enumerate([(home, 'home'), (away, 'away')]):
        team_id = IDS.get(team, str(301 + i))
        competitors.append({'id': team_id, 'homeAway': role, 'score': str(score[i]), 'winner': status == 'post' and score[i] > score[1-i],
                            'team': {'id': team_id, 'abbreviation': team, 'displayName': f'Synthetic {team}', 'name': team}})
    competition = {'id': identity, 'date': date, 'status': state(status, period), 'competitors': competitors}
    if label:
        competition['notes'] = [{'headline': label}]
    if wins is not None:
        competition['series'] = {'type': 'playoff', 'completed': max(wins) >= best // 2 + 1, 'totalCompetitions': best,
                                 'competitors': [{'id': c['id'], 'wins': wins[i]} for i, c in enumerate(competitors)]}
    return {'id': identity, 'name': f'{away} at {home} synthetic scenario', 'shortName': f'{away} @ {home}', 'date': date,
            'season': {'year': year, 'type': phase}, 'seasonType': {'type': phase}, 'week': {'number': 6},
            'status': state(status, period), 'competitions': [competition]}


def schedule(team, events, year=2027, phase=2, now='2026-09-30T18:55:00Z'):
    return {'status': 'success', 'season': {'year': year, 'type': phase}, 'timestamp': now,
            'team': {'id': IDS.get(team, '999'), 'abbreviation': team}, 'events': copy.deepcopy(events)}


def board(events):
    return {'synthetic': True, 'events': events}


def envelope(sport, endpoint, body):
    return {'synthetic': True, 'url': f'https://site.web.api.espn.com/apis/site/v2/sports/{PATHS[sport]}/{endpoint}', 'body': body}


def quote(ticker, team, bid=.6, ask=.64, close='2027-06-01T00:00:00Z'):
    return {'ticker': f'{ticker}-{team}', 'event_ticker': ticker, 'status': 'active', 'result': '', 'market_type': 'binary',
            'notional_value_dollars': '1.0000', 'close_time': close, 'yes_bid_dollars': f'{bid:.4f}',
            'yes_ask_dollars': f'{ask:.4f}', 'yes_bid_size_fp': '10.00', 'yes_ask_size_fp': '10.00'}


def qualification(sport, year):
    series = SERIES[sport]
    ticker = f'{series}-{(year + (sport == "nfl")) % 100:02}'
    subtitle = str(year) if sport == 'mlb' else f'{year if sport == "nfl" else year - 1}-{(year + 1 if sport == "nfl" else year) % 100:02}'
    aliases = {'GS': 'GSW', 'NO': 'NOP', 'NY': 'NYK', 'SA': 'SAS', 'UTAH': 'UTA', 'WSH': 'WAS'} if sport == 'nba' else {'UTAH': 'UTA'} if sport == 'nhl' else {'ARI': 'AZ', 'CHW': 'CWS'} if sport == 'mlb' else {'JAX': 'JAC', 'WSH': 'WAS', 'LAR': 'LA'} if sport == 'nfl' else {}
    markets = [quote(ticker, aliases.get(team, team)) for team in TEAMS[sport]]
    return {'synthetic': True, 'event': {'event_ticker': ticker, 'series_ticker': series, 'sub_title': subtitle, 'markets': markets}}


def qualification_fixtures():
    for sport, year in [('nfl', 2026), ('nba', 2027), ('nhl', 2027), ('mlb', 2026)]:
        root = qualification(sport, year)
        markets = {m['ticker'].rsplit('-', 1)[1]: m for m in root['event']['markets']}
        selected = {'nba': ('BOS', .72, .76), 'nhl': ('ANA', .42, .46), 'mlb': ('SEA', .12, .16)}.get(sport)
        if selected:
            team, bid, ask = selected
            markets[team].update(yes_bid_dollars=f'{bid:.4f}', yes_ask_dollars=f'{ask:.4f}')
        if sport == 'mlb':
            for team in ['ATL', 'MIL']:
                markets[team].update(yes_bid_dollars='0.9900', yes_ask_dollars='1.0000', yes_ask_size_fp='0.00')
            for team in ['COL', 'CIN']:
                markets[team].update(yes_bid_dollars='0.0000', yes_ask_dollars='0.0100', yes_bid_size_fp='0.00')
        emit(FS + f'kalshi-{sport}-playoffs-{year}.json', root)
    old = qualification('nba', 2026)
    finals = old['event']['markets']
    for i, market in enumerate(finals):
        market.update(status='finalized', result='yes' if i < 16 else 'no', settlement_value_dollars='1.0000' if i < 16 else '0.0000')
    old['event']['markets'] = []
    emit(FS + 'kalshi-nba-playoffs-2026-event.json', old)
    emit(FS + 'kalshi-nba-playoffs-2026-archived.json', {'synthetic': True, 'markets': finals, 'cursor': ''})


def rankings():
    aliases = {'LA': 'LAK', 'NJ': 'NJD', 'SJ': 'SJS', 'TB': 'TBL', 'UTAH': 'UTA'}
    chosen = {5: 'TB', 13: 'UTAH', 16: 'NJ', 20: 'LA', 23: 'NYR', 27: 'SJ', 29: 'CHI', 30: 'SEA'}
    remaining = iter(sorted(set(TEAMS['nhl']) - set(chosen.values())))
    ordered = [chosen[i] if i in chosen else next(remaining) for i in range(1, 33)]
    rows = ['teamCode,avg_overall_prediction,goalie,backup']
    for i, team in enumerate(ordered):
        value = '.75000' if i == 0 else '.20000' if team == 'CHI' else '.80000' if team == 'SEA' else '.50000'
        rows.append(f'{aliases.get(team, team)},0{value},FixtureGoalie,FixtureBackup')
    emit(F + 'MoneyPuck/rankings_current.csv', '\n'.join(rows) + '\n')
    emit(F + 'MoneyPuck/update_time.txt', 'Sunday September 27, 2026 at 12:00 PM Eastern Time\n')


def moneypuck_model():
    rows = ['teamCode,scenerio,madePlayoffs,firstSimulatedId,wonCup']
    for team in TEAMS['nhl']:
        probability = '.2518' if team == 'NSH' else '.6804' if team == 'OTT' else '.5'
        game_id = '2099020003' if team == 'NSH' else '2099020002'
        rows.append(f'{team},ALL,0{probability},{game_id},0.01')
    rows += ['NSH,WIN,0.4,2099020003,0.01', 'OTT,LOSS,0.6,2099020002,0.01']
    prefix = F + 'MoneyPuckQualification/Synthetic/'
    emit(prefix + 'simulations.csv', '\n'.join(rows) + '\n')
    emit(prefix + 'publication.txt', '2026-10-11 13:00:00.000000-04:00\n')
    emit(prefix + 'predictions.html', '<!doctype html><html><body><main><h1>2026–2027 NHL Playoff Predictions</h1><p>Synthetic model fixture, not provider content.</p></main></body></html>\n')
    for game_id in [2099020002, 2099020003]:
        emit(prefix + f'game-{game_id}.json', {'synthetic': True, 'id': game_id, 'season': 20262027})
    root = qualification('nhl', 2027)
    root['event']['markets'] = [quote('KXNHLPLAYOFF-27', 'NSH', .25, .75), quote('KXNHLPLAYOFF-27', 'OTT', .55, .60)]
    emit(prefix + 'kalshi-quote-report-reproduction.json', root)


def pro_schedules():
    upcoming = event('401891821', 'PHI', 'PIT', '2026-09-30T23:30:00Z')
    upcoming['competitions'][0]['odds'] = [{'moneyline': {'home': {'close': {'odds': '-150'}}, 'away': {'close': {'odds': '+150'}}}}]
    events = [upcoming] + [event(f'synthetic-phi-{i}', 'PHI', 'BOS', stamp(instant('2026-10-01T23:00:00Z') + timedelta(days=i))) for i in range(83)]
    emit(F + 'ProHype/nhl-schedule.json', schedule('PHI', events))
    emit(F + 'ProHype/nhl-upcoming-board.json', board([upcoming]))
    nfl = [event(f'synthetic-buf-{i}', 'BUF', 'NYJ', stamp(instant('2026-09-01T18:00:00Z') + timedelta(days=7*i)), 2026,
                 status='post' if i < 3 else 'pre', period=4 if i < 3 else 0, score=(21, 14) if i < 3 else (0, 0)) for i in range(17)]
    emit(F + 'ProHype/espnschedule.json', schedule('BUF', nfl, 2026))
    emit(F + 'ProHype/nfl-board.json', board([event('synthetic-nfl-final', 'BUF', 'NYJ', '2026-01-18T18:00:00Z', 2025, 3, 'post', 4, (21, 14), 'AFC Divisional Playoffs')]))
    emit(F + 'ProHype/nhl-board.json', board([event('401777460', 'FLA', 'EDM', '2026-06-01T20:00:00Z', 2026, 3, 'post', 3, (4, 2), 'Stanley Cup Final - Game 6', (4, 2))]))
    emit(F + 'ProHype/nba-board.json', board([event('synthetic-playin-9', 'ATL', 'CHI', '2026-04-14T20:00:00Z', 2026, 5, 'post', 4, (100, 98), 'NBA Play-In - East - 9th Place vs 10th Place'), event('synthetic-playin-7', 'BOS', 'MIA', '2026-04-14T20:00:00Z', 2026, 5, 'post', 4, (100, 98), 'NBA Play-In - East - 7th Place vs 8th Place')]))
    emit(F + 'ProHype/nba-decider-board.json', board([event('synthetic-playin-8', 'ATL', 'CHI', '2026-04-17T20:00:00Z', 2026, 5, 'post', 4, (100, 98), 'NBA Play-In - East - 8th Seed Game')]))
    cases = []
    definitions = [
        ('mlb', 1, '401809252', 'ALWC', 2, 3, (1, 1), (2, 1)),
        ('mlb', 2, '401809270', 'ALDS', 3, 5, (1, 2), (2, 5)),
        ('mlb', 3, '401809292', 'NLCS', 3, 7, (3, 0), (4, 1)),
        ('mlb', 4, '401809300', 'World Series', 4, 7, (2, 2), (2, 3)),
        ('nba', 1, '401869369', 'East 1st Round', 2, 7, (2, 0), (110, 100)),
        ('nba', 2, '401871162', 'East Semifinals', 4, 7, (0, 4), (101, 111)),
        ('nba', 3, '401873199', 'West Finals', 3, 7, (1, 2), (100, 110)),
        ('nba', 4, 'synthetic-nba-final', 'NBA Finals', 7, 7, (4, 3), (111, 110)),
        ('nhl', 1, 'synthetic-nhl-round-1', 'East 1st Round', 2, 7, (1, 1), (3, 2)),
        ('nhl', 2, '401871407', 'East 2nd Round', 4, 7, (2, 2), (2, 3)),
        ('nhl', 3, '401872904', 'West Final', 2, 7, (0, 2), (1, 3)),
        ('nhl', 4, 'synthetic-nhl-round-4', 'Stanley Cup Final', 6, 7, (4, 2), (4, 2)),
    ]
    for sport, round_number, identity, label, ordinal, best, wins, scores in definitions:
        home, away = ('FLA', 'EDM') if sport == 'nhl' else ('ATL', 'PHI') if sport == 'mlb' else ('BOS', 'MIA')
        cases.append({'synthetic': True, 'sport': sport, 'round': round_number, 'url': f'https://fixture.invalid/{sport}/round-{round_number}',
                      'event': event(identity, home, away, '2026-05-01T20:00:00Z', 2026, 3, 'post', 4, scores, f'{label} - Game {ordinal}', wins, best)})
    emit(F + 'ProHype/postseason-rounds.json', cases)
    emit(F + 'ProHype/nfl-fpi.json', {'synthetic': True, 'pagination': {'pages': 1}, 'requestedSeason': {'year': 2026}, 'currentValues': {'season': 2026},
         'categories': [{'name': 'fpi', 'names': ['fpi', 'fpirank']}], 'teams': [{'team': {'id': str(i+1), 'abbreviation': t, 'displayName': f'Synthetic {t}'}, 'categories': [{'name': 'fpi', 'values': [16-i, i+1], 'ranks': [str(i+1)]}]}  for i, t in enumerate(TEAMS['nfl'])]})


def mlb_preview():
    game = event('401907973', 'ATL', 'PHI', '2026-10-02T00:00:00Z', 2026, 3, period=1, label='NLWC - Game 3', wins=(1, 1), best=3)
    game['competitions'][0]['odds'] = [{'moneyline': {'home': {'close': {'odds': '-120'}}, 'away': {'close': {'odds': '+110'}}}}]
    prior = [event('401907965', 'ATL', 'PHI', '2026-09-29T20:00:00Z', 2026, 3, 'post', 9, (3, 1), 'NLWC - Game 1'), event('401907972', 'ATL', 'PHI', '2026-09-30T20:00:00Z', 2026, 3, 'post', 9, (1, 3), 'NLWC - Game 2')]
    emit(F + 'ProHype/mlb-pregame-401907973.json', {'synthetic': True, 'board': envelope('mlb', 'scoreboard?dates=20261001&limit=1000', board([game])),
         'schedules': [envelope('mlb', f'teams/{IDS[t]}/schedule?season=2026&seasontype=3', schedule(t, prior + [game], 2026, 3, '2026-10-01T19:14:00Z')) for t in ['ATL', 'PHI']]})


def nfl_preview():
    game = event('401872985', 'PIT', 'IND', '2026-10-11T17:00:00Z', 2026)
    emit(F + 'ProHype/nfl-pregame-401872985.json', {'synthetic': True, 'board': envelope('nfl', 'scoreboard?dates=20261011&limit=1000', board([game])),
         'kalshi': {'synthetic': True, 'body': {'markets': [quote('KXNFLGAME-26OCT11INDPIT', 'IND', .40, .44), quote('KXNFLGAME-26OCT11INDPIT', 'PIT', .59, .63)], 'cursor': ''}}})


def nhl_stakes():
    game = event('401891819', 'NYR', 'TB', '2026-10-01T23:00:00Z')
    game['competitions'][0]['odds'] = [{'moneyline': {'home': {'close': {'odds': '-150'}}, 'away': {'close': {'odds': '+150'}}}}]
    final = event('synthetic-nyr-final', 'NYR', 'BOS', '2026-09-30T20:00:00Z', status='post', period=3, score=(2, 1))
    schedules = []
    for team in TEAMS['nhl']:
        events = [copy.deepcopy(game)] if team in {'NYR', 'TB'} else []
        if team in {'NYR', 'BOS'}:
            events.append(copy.deepcopy(final))
        for i in range(84-len(events)):
            opponent = 'ANA' if team != 'ANA' else 'BOS'
            events.append(event(f'synthetic-{team}-{i}', team, opponent, stamp(instant('2026-10-02T20:00:00Z') + timedelta(days=i))))
        schedules.append(envelope('nhl', f'teams/{IDS[team]}/schedule?season=2027&seasontype=2', schedule(team, events, now='2026-10-01T15:50:00Z')))
    root = qualification('nhl', 2027)
    accepted = {'NYR', 'TB', 'NYI', 'PHI'} | set(TEAMS['nhl'][:19])
    for team in TEAMS['nhl']:
        if len(accepted) < 23: accepted.add(team)
    for market in root['event']['markets']:
        team = market['ticker'].rsplit('-', 1)[1]
        canonical = 'UTAH' if team == 'UTA' else team
        bid, ask = {'NYR': (.34, .36), 'TB': (.79, .81), 'NYI': (.19, .21), 'PHI': (.54, .56)}.get(canonical, (.60, .64))
        market.update(yes_bid_dollars=f'{bid:.4f}', yes_ask_dollars=f'{ask:.4f}')
        if canonical not in accepted: market['yes_bid_size_fp'] = '0.00'
    emit(F + 'ProHype/nhl-stakes-401891819.json', {'synthetic': True, 'scoringTime': '2026-10-01T15:55:00Z', 'schedules': schedules,
         'board': envelope('nhl', 'scoreboard?dates=20261001&limit=1000', board([game])),
         'roster': envelope('nhl', 'teams?limit=1000&season=2027', {'sports': [{'leagues': [{'season': {'year': 2027}, 'teams': [{'team': {'id': IDS[t], 'abbreviation': t}} for t in TEAMS['nhl']]}]}]}),
         'kalshi': {'url': 'https://api.elections.kalshi.com/trade-api/v2/events/KXNHLPLAYOFF-27?with_nested_markets=true', 'body': root}})


def mlb_reconciliation():
    for identity, home, away, date, original, ticker, bid, ask in [
        ('401907993', 'CHW', 'CLE', '2026-10-09T00:00:00Z', 'Oct 8, 2026 at 5:00 PM EDT', 'KXMLBGAME-26OCT081700CLECWS', .62, .63),
        ('401908031', 'MIL', 'LAD', '2026-10-12T21:00:00Z', 'Oct 12, 2026', 'KXMLBGAME-26OCT122000LADMIL', .82, .83),
    ]:
        markets = []
        game_text = f'Synthetic {away} vs Synthetic {home} professional baseball game originally scheduled for {original}'
        for team, lo, hi in [(home, bid, ask), (away, 1-ask, 1-bid)]:
            market = quote(ticker, 'CWS' if team == 'CHW' else team, lo, hi, '2026-10-14T23:00:00Z')
            market.update(yes_sub_title=f'Synthetic {team}', custom_strike={'baseball_team': f'fixture-{team}'}, occurrence_datetime=date, expected_expiration_time=date,
                          rules_primary=f'If Synthetic {team} wins the {game_text}, then the market resolves to Yes.',
                          rules_secondary=f'The following market refers to the {game_text}. If this game is postponed or delayed, the market will remain open and close after the rescheduled game has finished (within two days). If the game is cancelled or rescheduled to over two days away, the market will resolve to a fair price in accordance with the rules. Kalshi is not affiliated, associated, authorized, endorsed by, or in any way officially connected with the Governing League. All trademarks, logos, and brand names are the property of their respective owners.')
            markets.append(market)
        emit(FS + f'kalshi-mlb-{identity}.json', {'synthetic': True, 'scoreboard': board([event(identity, home, away, date, 2026, status='in', period=3, score=(2, 1))]), 'catalog': {'markets': markets, 'cursor': ''},
             'schedule': {'cursor': '', 'milestones': [{'id': f'synthetic-{identity}', 'category': 'Sports', 'type': 'baseball_game', 'start_date': date, 'primary_event_tickers': [ticker], 'details': {'league': 'MLB', 'status': 'inprogress', 'main_game_event_ticker': ticker, 'home_team_id': f'fixture-{home}', 'away_team_id': f'fixture-{away}'}}]}})


def chart_histories():
    buffer = io.StringIO(newline='')
    writer = csv.writer(buffer, lineterminator='\n')
    fields = ['game_id', 'timestamp', 'home_win_probability', 'away_win_probability', 'home_score', 'away_score', 'label', 'source', 'is_probability_bound']
    writer.writerow(fields)
    contexts = []
    for identity, sport, count, start, home, away, habbr, aabbr, completed in [
        ('990000001', 'cfb', 300, '2026-10-10T16:00:00.123456Z', 'Synthetic Oklahoma State', 'Synthetic UCF', 'OKST', 'UCF', False),
        ('990000002', 'nfl', 320, '2026-09-20T17:00:00.654321Z', 'Synthetic Chicago', 'Synthetic Minnesota', 'CHI', 'MIN', True),
    ]:
        for i in range(count):
            timestamp = instant(start) + timedelta(seconds=30*i)
            probability = (.5 + .25*((i//40)%2)) if not completed else (.2 + .6*((i//35)%2))
            bounded = not completed and i >= count - 10
            if bounded: probability = .995
            period = 0 if i == 0 and completed else min(4, 1 + i//75)
            label = f'Q{period} {14 - i%15}:00' if period else 'Q0 0:00'
            writer.writerow([identity, stamp(timestamp), probability, 1-probability, (i//100)*7, (i//80)*3, label,
                             'Kalshi (near-certain orderbook bound)' if bounded else 'Kalshi', str(bounded).lower()])
        contexts.append({'gameId': identity, 'sport': sport, 'homeTeam': home, 'awayTeam': away, 'homeAbbreviation': habbr, 'awayAbbreviation': aabbr,
                         'status': 'final' if completed else 'in_progress', 'captureCutoff': stamp(timestamp), 'sampleCount': count, 'context': 'Independently generated staircase fixture. Not a historical game.'})
    csv_text = buffer.getvalue()
    emit(F + 'Excitement/histories.csv', csv_text)
    emit(F + 'Excitement/context.json', {'synthetic': True, 'sourceArtifact': 'scripts/test-fixtures/generate.py', 'sourceSha256': hashlib.sha256(csv_text.encode()).hexdigest(),
         'contextEvidence': 'Deterministic constructed staircase cases, no captured inputs.', 'observedFields': [],
         'notObservedFields': ['IsHistoricalImport', 'IsHistorical', 'database row IDs', 'All fixture fields are constructed'],
         'seedingAssumptions': {'isHistoricalImport': False, 'isHistorical': False, 'explanation': 'Explicit synthetic eligibility controls', 'scheduledStart': 'First constructed timestamp', 'moneylines': 'Unset placeholders', 'otherFields': 'Constructed', 'labels': 'Constructed football clock', 'bounds': 'Final ten samples of the CFB scenario are constructed bounds'}, 'games': contexts})


def nfl_leverage():
    prefix = F + 'NflPlayoffLeverage/'
    slugs = dict(zip(TEAMS['nfl'], ['cardinals', 'falcons', 'ravens', 'bills', 'panthers', 'bears', 'bengals', 'browns', 'cowboys', 'broncos', 'lions', 'packers', 'texans', 'colts', 'jaguars', 'chiefs', 'chargers', 'rams', 'raiders', 'dolphins', 'vikings', 'patriots', 'saints', 'giants', 'jets', 'eagles', 'steelers', 'seahawks', 'forty-niners', 'buccaneers', 'titans', 'commanders'], strict=True))
    circle = TEAMS['nfl'][:]
    rounds = []
    for rotation in range(31):
        pairs = []
        for index in range(16):
            home, away = circle[index], circle[-index - 1]
            if (rotation + index) % 2:
                home, away = away, home
            if {home, away} == {'DET', 'GB'}:
                home, away = 'DET', 'GB'
            pairs.append((home, away))
        rounds.append(pairs)
        circle = [circle[0], circle[-1], *circle[1:-1]]
    target = next(pairs for pairs in rounds if ('DET', 'GB') in pairs)
    others = [pairs for pairs in rounds if pairs is not target][:16]
    selected = [*others[:6], target, *others[6:]]
    eastern = ZoneInfo('America/New_York')
    origin = datetime(2026, 9, 6, 13, 0, tzinfo=eastern)
    rows = []
    tables = []
    for week in range(1, 19):
        local = origin + timedelta(weeks=week - 1)
        date = f'{local:%a %b} {local.day}, ' + ('TBD' if week >= 12 else '1:00 pm')
        table = [f'<h2 id="{week}">Week {week}</h2><table>', '<tr><th>Home Team</th><th>Score</th><th>Visiting Team</th><th>Date / Time (ET)</th></tr>']
        for index, (home, away) in enumerate(selected[week - 1] if week <= 17 else []):
            home_score = str(17 + (week + index) % 7) if week < 7 else ''
            away_score = str(10 + (3 * week + index) % 9) if week < 7 else ''
            start = local.astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
            rows.append([week, home, away, start, home_score, away_score])
            score = f'{home_score}–{away_score}' if home_score else ''
            table.append(f'<tr><td><a href="#{slugs[home]}">{home}</a></td><td>{score}</td><td><a href="#{slugs[away]}">{away}</a></td><td>{date}</td></tr>')
        table.append('</table>')
        tables.append('\n'.join(table))
    def document(title, content):
        return f'<!doctype html><html><head><title>{title}</title></head><body><header><span class="datetime">Synthetic publication batch A</span></header><p class="wkinfo">NFL - Week 7 of 18</p><main class="mncntnt"><h1>{title}</h1><p>Independently authored synthetic contract fixture; not provider content or real sports results.</p>{content}</main></body></html>\n'
    emit(prefix + 'nflschedule.html', document('2026 NFL Schedule', '\n'.join(tables)))
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator='\n')
    writer.writerow(['week', 'home', 'away', 'start', 'home_score', 'away_score'])
    writer.writerows(rows)
    emit(prefix + 'synthetic-espn-controls.csv', buffer.getvalue())
    for team, name, opponent, probabilities in [('GB', 'Green Bay Packers', 'Lions', (18, 32, 43)), ('DET', 'Detroit Lions', 'Packers', (26, 36, 46))]:
        completed = [row for row in rows if row[4] and team in row[1:3]]
        wins = sum(int(row[4] if row[1] == team else row[5]) > int(row[5] if row[1] == team else row[4]) for row in completed)
        ties = sum(row[4] == row[5] for row in completed)
        losses = len(completed) - wins - ties
        content = [f'<h3>Next Game - {opponent}</h3><table>', '<tr>' + ''.join(f'<th>{label}</th>' for label in ['Scenario', 'W', 'L', 'T', *[f'Seed {n}' for n in range(1, 8)], 'No Playoffs']) + '</tr>', '<tr><th colspan="12">Independent synthetic scenarios</th></tr>']
        for label, w, loss, no_playoffs in [('Win Next Game', wins + 1, losses, probabilities[0]), ('Current Standings', wins, losses, probabilities[1]), ('Lose Next Game', wins, losses + 1, probabilities[2])]:
            cells = [label, w, loss, ties, f'{100 - no_playoffs}%', *['0%'] * 6, f'{no_playoffs}%']
            content.append('<tr>' + ''.join(f'<td>{value}</td>' for value in cells) + '</tr>')
        content.append('</table>')
        emit(prefix + slugs[team] + 'whatif.html', document(name + ' What If?', '\n'.join(content)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    for generate in [qualification_fixtures, rankings, moneypuck_model, pro_schedules, mlb_preview, nfl_preview, nhl_stakes, mlb_reconciliation, chart_histories, nfl_leverage]:
        generate()
    manifest = {path: hashlib.sha256(text.encode()).hexdigest() for path, text in sorted(OUTPUTS.items())}
    emit('tests/fixtures/manifest.json', {'provenance': 'Independently constructed by scripts/test-fixtures/generate.py; no provider responses used as inputs.', 'sha256': manifest})
    different = []
    for path, text in OUTPUTS.items():
        target = ROOT / path
        if args.check:
            if not target.is_file() or target.read_bytes() != text.encode(): different.append(path)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text)
    if different:
        raise SystemExit('Synthetic fixture drift: ' + ', '.join(different))
    print(f'{len(OUTPUTS)} independently generated fixture/manifest files verified' if args.check else f'Wrote {len(OUTPUTS)} synthetic fixture/manifest files')


if __name__ == '__main__':
    main()
