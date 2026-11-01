#!/usr/bin/env python3
import argparse
import collections
import csv
import hashlib
import io
import json
import re
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from zoneinfo import ZoneInfo


def instant(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("Timestamps must include a timezone")
    return result.astimezone(timezone.utc)


def number(value):
    try:
        parsed = Decimal(str(value))
        return parsed if parsed.is_finite() else None
    except InvalidOperation:
        return None


def team_code(sport, team):
    return {("mlb", "ARI"): "AZ", ("mlb", "CHW"): "CWS",
            ("nfl", "WSH"): "WAS", ("nfl", "JAX"): "JAC"}.get((sport, team), team)


def validate_game(game, summary):
    if not re.fullmatch(r"\d+", game["id"]) or game["sport"] not in ("mlb", "nfl"):
        raise ValueError("Only explicitly identified MLB/NFL games are supported")
    comp = summary["header"]["competitions"][0]
    status = comp["status"]["type"]
    if comp["id"] != game["id"] or status["name"] != "STATUS_FINAL" or not status["completed"]:
        raise ValueError("ESPN game identity/status changed")
    if instant(comp["date"]) != instant(game["scheduled_start"]):
        raise ValueError("Scheduled start changed since approval")
    teams = {c["homeAway"]: c["team"]["abbreviation"] for c in comp["competitors"]}
    if teams != {"home": game["home_team_abbr"], "away": game["away_team_abbr"]}:
        raise ValueError("ESPN teams changed since approval")
    home_event, home_code = game["home_ticker"].rsplit("-", 1)
    away_event, away_code = game["away_ticker"].rsplit("-", 1)
    if home_event != away_event or home_code == away_code:
        raise ValueError("Outcomes must belong to one event and distinct teams")
    if (home_code, away_code) != (team_code(game["sport"], teams["home"]), team_code(game["sport"], teams["away"])):
        raise ValueError("Outcome tickers do not match the approved teams")
    series = "KXMLBGAME" if game["sport"] == "mlb" else "KXNFLGAME"
    eastern = instant(game["scheduled_start"]).astimezone(ZoneInfo("America/New_York"))
    month = ("JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC")[eastern.month - 1]
    date = f"{eastern.year % 100:02d}{month}{eastern.day:02d}"
    pattern = rf"{series}-{date}(\d{{4}})?(?:{away_code}{home_code}|{home_code}{away_code})(G[12])?"
    parsed = re.fullmatch(pattern, home_event)
    if not parsed:
        raise ValueError("Event does not match the approved sport, date and teams")
    if parsed[1] and parsed[1] != eastern.strftime("%H%M") and not game.get("selection_note"):
        raise ValueError("Rescheduled event requires explicit audited identity evidence")
    if parsed[2] and not parsed[1]:
        raise ValueError("Doubleheader event needs an explicit start time")
    plays = summary.get("plays", [])
    if not plays:
        drives = summary.get("drives") or {}
        previous = drives.get("previous", [])
        current = drives.get("current")
        if current and current.get("id") not in {d.get("id") for d in previous}:
            previous = previous + [current]
        plays = [p for drive in previous for p in drive.get("plays", [])]
    clocks = [instant(p["wallclock"]) for p in plays
              if p.get("wallclock") and p.get("period", {}).get("number", 0) > 0]
    start, end = instant(game["first_play"]), instant(game["last_play"])
    if game.get("end_play_id"):
        terminal = [p for p in plays if p.get("id") == game["end_play_id"]]
        if (game["sport"] != "nfl" or not game.get("play_window_note") or len(terminal) != 1
                or terminal[0].get("type", {}).get("text") != "End of Game"
                or terminal[0].get("period", {}).get("number", 0) < 4
                or terminal[0].get("clock", {}).get("displayValue") != "0:00"):
            raise ValueError("Reviewed end boundary must identify an explicit final whistle")
        observed_end = instant(terminal[0]["wallclock"])
    else:
        observed_end = max(clocks) if clocks else None
    if len(clocks) < 2 or (min(clocks), observed_end) != (start, end):
        raise ValueError("Observed play bounds changed since approval")
    if not 0 < (end - start).total_seconds() < 12 * 3600 or (start - instant(game["scheduled_start"])).total_seconds() < -600:
        raise ValueError("Untrustworthy play bounds; manual review required")


def validate_settlement(game, summary, markets):
    competitors = {c["homeAway"]: c for c in summary["header"]["competitions"][0]["competitors"]}
    for side, market in zip(("home", "away"), markets):
        ticker = game[side + "_ticker"]
        expected_result = "yes" if competitors[side]["winner"] else "no"
        if (market.get("ticker") != ticker or market.get("event_ticker") != ticker.rsplit("-", 1)[0]
                or market.get("status") not in ("finalized", "settled") or market.get("result") != expected_result):
            raise ValueError("Market settlement does not agree with the approved ESPN outcome")
        if instant(market["created_time"]) >= instant(game["first_play"]):
            raise ValueError("Market was not available during this game")
        delay = (instant(market["close_time"]) - instant(game["last_play"])).total_seconds()
        if not 0 <= delay <= 1800:
            raise ValueError("Market close does not corroborate the observed game end")


def traded_closes(payload, ticker, start, end):
    if payload.get("ticker") != ticker or not isinstance(payload.get("candlesticks"), list):
        raise ValueError("Candle response identity or shape mismatch")
    candles = payload["candlesticks"]
    counts = collections.Counter(c.get("end_period_ts") for c in candles)
    result = {}
    for candle in candles:
        stamp = candle.get("end_period_ts")
        if type(stamp) is not int or stamp % 60 or counts[stamp] != 1:
            continue
        if not start.timestamp() <= stamp - 60 < stamp < end.timestamp():
            continue
        price = candle.get("price") or {}
        raw = price.get("close_dollars")
        if raw is None and price.get("close") is not None:
            legacy = number(price["close"])
            raw = legacy / 100 if legacy is not None else None
        close = number(raw)
        volume = number(candle.get("volume_fp", candle.get("volume")))
        if close is not None and 0 < close < 1 and volume is not None and volume > 0:
            result[stamp] = close
    return result


def samples(game, home_payload, away_payload):
    start, end = instant(game["first_play"]), instant(game["last_play"])
    home = traded_closes(home_payload, game["home_ticker"], start, end)
    away = traded_closes(away_payload, game["away_ticker"], start, end)
    rows = []
    for stamp in sorted(home.keys() & away.keys()):
        hp = (home[stamp] / (home[stamp] + away[stamp])).quantize(Decimal("0.000000000001"))
        ap = 1 - hp
        if not 0 < hp < 1:
            raise ValueError("Normalized probability rounded to a terminal value")
        def moneyline(p):
            return int(round(-100 * p / (1 - p) if p >= Decimal("0.5") else 100 * (1 - p) / p))
        rows.append((game["id"], datetime.fromtimestamp(stamp, timezone.utc).isoformat(),
                     moneyline(hp), moneyline(ap), str(hp), str(ap)))
    if len(rows) != game["expected_points"] or not rows:
        raise ValueError(f"{game['id']}: expected {game['expected_points']} points, got {len(rows)}; review before writing")
    return rows


def prepare(manifest, evidence):
    if not re.fullmatch(r"[a-z0-9_]{1,50}", manifest["batch_id"]):
        raise ValueError("Invalid batch source identifier")
    games = manifest["games"]
    if not games or len(games) > 50 or len({g["id"] for g in games}) != len(games):
        raise ValueError("Manifest must contain 1–50 unique, explicitly approved games")
    rows, hashes = [], {}
    for game in games:
        payloads = []
        for suffix in ("espn", "home-candles", "away-candles", "home-market", "away-market"):
            name = f"{game['id']}-{suffix}.json"
            raw = (evidence / name).read_bytes()
            hashes[name] = hashlib.sha256(raw).hexdigest()
            payloads.append(json.loads(raw))
        validate_game(game, payloads[0])
        validate_settlement(game, payloads[0], payloads[3:])
        rows.extend(samples(game, payloads[1], payloads[2]))
    return rows, hashes


def copy_csv(table, rows):
    out = io.StringIO()
    csv.writer(out, lineterminator="\n").writerows(rows)
    return f"COPY {table} FROM STDIN WITH (FORMAT csv);\n{out.getvalue()}\\.\n"


def render_sql(manifest, rows, commit=False, undo=False):
    source = manifest["batch_id"]
    if not re.fullmatch(r"[a-z0-9_]{1,50}", source):
        raise ValueError("Invalid batch source identifier")
    expected = sum(g["expected_points"] for g in manifest["games"])
    if len(rows) != expected or len({(r[0], r[1]) for r in rows}) != expected:
        raise ValueError("Unexpected row count or duplicate timestamps")
    sql = """\\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '30s';
CREATE TEMP TABLE recovery_games (
 id text PRIMARY KEY, sport text, home_abbr text, away_abbr text,
 scheduled_start timestamptz, first_play timestamptz, last_play timestamptz, expected_points integer
) ON COMMIT DROP;
CREATE TEMP TABLE recovery_points (
 game_id text REFERENCES recovery_games(id), timestamp timestamptz,
 home_moneyline integer NOT NULL, away_moneyline integer NOT NULL,
 home_win_probability double precision NOT NULL CHECK (home_win_probability > 0 AND home_win_probability < 1),
 away_win_probability double precision NOT NULL CHECK (away_win_probability > 0 AND away_win_probability < 1),
 PRIMARY KEY(game_id,timestamp),
 CHECK (abs(home_win_probability + away_win_probability - 1) < 0.000000001)
) ON COMMIT DROP;
"""
    sql += copy_csv("recovery_games", [(g["id"], g["sport"], g["home_team_abbr"], g["away_team_abbr"],
        g["scheduled_start"], g["first_play"], g["last_play"], g["expected_points"]) for g in manifest["games"]])
    sql += copy_csv("recovery_points", rows)
    sql += """
SELECT g.id FROM games g JOIN recovery_games r ON r.id=g.id ORDER BY g.id FOR UPDATE OF g;
DO $guard$
BEGIN
 IF EXISTS (
  SELECT 1 FROM recovery_games r LEFT JOIN games g ON g.id=r.id
  WHERE g.id IS NULL OR g.sport IS DISTINCT FROM r.sport
   OR g.home_team_abbr IS DISTINCT FROM r.home_abbr OR g.away_team_abbr IS DISTINCT FROM r.away_abbr
   OR g.scheduled_start IS DISTINCT FROM r.scheduled_start OR g.status IS DISTINCT FROM 'final'
   OR g.is_historical_import IS DISTINCT FROM false
 ) THEN RAISE EXCEPTION 'Game identity/status changed; aborting recovery'; END IF;
 IF EXISTS (
  SELECT 1 FROM recovery_games r LEFT JOIN recovery_points p ON p.game_id=r.id
  GROUP BY r.id,r.expected_points HAVING count(p.*) <> r.expected_points
 ) OR EXISTS (
  SELECT 1 FROM recovery_points p JOIN recovery_games r ON r.id=p.game_id
  WHERE p.timestamp - interval '1 minute' < r.first_play OR p.timestamp >= r.last_play
 ) THEN RAISE EXCEPTION 'Recovery count or play bounds invalid'; END IF;
END $guard$;
"""
    if undo:
        sql += f"""
DO $guard$
BEGIN
 IF (SELECT count(*) FROM odds_snapshots WHERE source='{source}') <> {expected}
 OR EXISTS (
  SELECT 1 FROM recovery_points p LEFT JOIN odds_snapshots o
   ON o.game_id=p.game_id AND o.timestamp=p.timestamp AND o.source='{source}'
   AND o.is_historical AND o.home_moneyline=p.home_moneyline AND o.away_moneyline=p.away_moneyline
   AND o.home_win_probability=p.home_win_probability AND o.away_win_probability=p.away_win_probability
   AND o.home_score IS NULL AND o.away_score IS NULL AND o.period IS NULL AND o.game_clock IS NULL
   AND o.draw_moneyline IS NULL AND o.draw_win_probability IS NULL AND o.spread IS NULL AND o.over_under IS NULL
  WHERE o.id IS NULL
 ) THEN RAISE EXCEPTION 'Recovery batch changed; refusing automatic rollback'; END IF;
END $guard$;
DELETE FROM odds_snapshots o USING recovery_points p
WHERE o.game_id=p.game_id AND o.timestamp=p.timestamp AND o.source='{source}' AND o.is_historical;
"""
    else:
        sql += f"""
DO $guard$
BEGIN
 IF EXISTS (SELECT 1 FROM odds_snapshots o JOIN recovery_games r ON r.id=o.game_id)
 THEN RAISE EXCEPTION 'Approved game already has snapshots; refusing overwrite/duplicate'; END IF;
 IF EXISTS (SELECT 1 FROM odds_snapshots WHERE source='{source}')
 THEN RAISE EXCEPTION 'Batch source already exists'; END IF;
END $guard$;
INSERT INTO odds_snapshots (game_id,timestamp,home_moneyline,away_moneyline,
 home_win_probability,away_win_probability,source,is_historical)
SELECT game_id,timestamp,home_moneyline,away_moneyline,
 home_win_probability,away_win_probability,'{source}',true FROM recovery_points;
DO $guard$
BEGIN
 IF (SELECT count(*) FROM odds_snapshots WHERE source='{source}') <> {expected}
 THEN RAISE EXCEPTION 'Inserted batch count does not match preview'; END IF;
END $guard$;
SELECT game_id,count(*) AS historical_points,min(timestamp),max(timestamp)
FROM odds_snapshots WHERE source='{source}' GROUP BY game_id ORDER BY game_id;
"""
    return sql + ("COMMIT;\n" if commit else "ROLLBACK;\n")


def main():
    parser = argparse.ArgumentParser(description='Generate guarded SQL from an approved manifest and captured provider evidence.\n\nOffline only: never connects to a database. SQL defaults to ROLLBACK. See README.md.\n')
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New output directory (never overwritten)")
    parser.add_argument("--commit", action="store_true", help="Generate COMMIT instead of dry-run ROLLBACK")
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text())
    rows, hashes = prepare(manifest, args.evidence)
    sql = render_sql(manifest, rows, commit=args.commit)
    rollback = render_sql(manifest, rows, commit=args.commit, undo=True)
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "apply.sql").write_text(sql)
    (args.output / "rollback.sql").write_text(rollback)
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (args.output / "receipt.json").write_text(json.dumps({
        "batch_id": manifest["batch_id"], "generated_at": datetime.now(timezone.utc).isoformat(),
        "games": len(manifest["games"]), "points": len(rows), "commit": args.commit,
        "manifest_sha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
        "apply_sql_sha256": hashlib.sha256(sql.encode()).hexdigest(),
        "rollback_sql_sha256": hashlib.sha256(rollback.encode()).hexdigest(),
        "evidence_sha256": hashes,
    }, indent=2) + "\n")
    print(f"Prepared {len(rows)} historical points for {len(manifest['games'])} games; commit={args.commit}")
    print(f"SQL and provenance receipt: {args.output} (nothing executed)")


if __name__ == "__main__":
    main()
