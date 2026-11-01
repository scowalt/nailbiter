
import os
from datetime import datetime
from pathlib import Path
from typing import Sequence

import pandas as pd
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

from .ratings import RATING_COLUMNS, RATING_KEYS, evaluation_time, select_team_ratings


def _load_env_file() -> None:
    current = Path(__file__).resolve()
    for parent in current.parents:
        env_file = parent / ".env"
        if env_file.exists():
            with open(env_file) as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith("#") and "=" in line:
                        key, _, value = line.partition("=")
                        if key not in os.environ:   
                            os.environ[key] = value
            break


def get_connection_string() -> str:
    _load_env_file()
    if "DATABASE_URL" in os.environ:
        url = os.environ["DATABASE_URL"]
    else:
        password = os.getenv("POSTGRES_PASSWORD", "postgres")
        host = os.getenv("POSTGRES_HOST", "localhost")
        port = os.getenv("POSTGRES_PORT", "5432")
        db = os.getenv("POSTGRES_DB", "excitement_score")
        user = os.getenv("POSTGRES_USER", "postgres")
        url = f"postgresql+psycopg://{user}:{password}@{host}:{port}/{db}"

    if url.startswith("postgresql://"):
        url = url.replace("postgresql://", "postgresql+psycopg://", 1)
    return url


def get_engine() -> Engine:
    return create_engine(get_connection_string())


def load_games(
    engine: Engine | None = None,
    sport: str | None = None,
    status: str | None = None,
    start_date: str | None = None,
    end_date: str | None = None,
    limit: int | None = None,
) -> pd.DataFrame:
    if engine is None:
        engine = get_engine()

    conditions = []
    params: dict[str, str | int] = {}

    if sport:
        conditions.append("sport = :sport")
        params["sport"] = sport.lower()

    if status:
        conditions.append("status = :status")
        params["status"] = status

    if start_date:
        conditions.append("scheduled_start >= :start_date")
        params["start_date"] = start_date

    if end_date:
        conditions.append("scheduled_start <= :end_date")
        params["end_date"] = end_date

    where_clause = " AND ".join(conditions) if conditions else "1=1"
    limit_clause = "LIMIT :limit" if limit is not None else ""
    if limit is not None:
        params["limit"] = limit

    query = f"""
        SELECT
            id,
            sport,
            season_year,
            season_type,
            status,
            home_team,
            away_team,
            home_team_abbr as home_team_abbreviation,
            away_team_abbr as away_team_abbreviation,
            home_score,
            away_score,
            scheduled_start,
            actual_start,
            end_time
        FROM games
        WHERE {where_clause}
        ORDER BY scheduled_start DESC
        {limit_clause}
    """

    return pd.read_sql(text(query), engine, params=params)


def load_team_ratings(
    engine: Engine | None = None,
    sport: str | None = None,
    season: int | None = None,
    *,
    evaluated_at: datetime,
) -> pd.DataFrame:
    evaluation_time(evaluated_at)
    if engine is None:
        engine = get_engine()

    conditions = []
    params: dict[str, str | int] = {}

    if sport:
        conditions.append("sport = :sport")
        params["sport"] = sport.lower()

    if season is not None:
        conditions.append("season = :season")
        params["season"] = season

    where_clause = " AND ".join(conditions) if conditions else "1=1"

    query = f"""
        SELECT id, sport, team_abbreviation, season,
               elo_rating, bpi_rating, fpi_rating, championship_probability,
               playoff_probability, playoff_probability_is_final,
               source, source_season, fetched_at
        FROM team_ratings
        WHERE {where_clause}
    """
    rows = pd.read_sql(text(query), engine, params=params)
    return select_team_ratings(rows, evaluated_at=evaluated_at)


def load_team_ratings_for_games(
    engine: Engine, games: pd.DataFrame, *, evaluated_at: datetime
) -> pd.DataFrame:
    evaluation_time(evaluated_at)
    if games.empty or "season_year" not in games:
        return pd.DataFrame(columns=RATING_COLUMNS)
    keys = []
    for game in games.dropna(subset=["season_year"]).itertuples(index=False):
        for team in (game.home_team_abbreviation, game.away_team_abbreviation):
            keys.append((game.sport.lower(), team, int(game.season_year)))
    if not keys:
        return pd.DataFrame(columns=RATING_COLUMNS)
    requested = pd.DataFrame(keys, columns=RATING_KEYS).drop_duplicates()
    frames = [
        load_team_ratings(engine, sport=sport, season=int(season), evaluated_at=evaluated_at)
        for sport, season in requested[["sport", "season"]].drop_duplicates().itertuples(
            index=False, name=None
        )
    ]
    selected = pd.concat(frames, ignore_index=True)
    result = requested.merge(selected, on=RATING_KEYS, how="left", validate="one_to_one")
    result["playoff_probability_is_final"] = result["playoff_probability_is_final"].eq(True)
    return result


def load_pregame_odds(
    engine: Engine | None = None,
    game_ids: Sequence[str] | None = None,
) -> pd.DataFrame:
    if engine is None:
        engine = get_engine()

    if game_ids:
        placeholders = ", ".join([f":id_{i}" for i in range(len(game_ids))])
        params = {f"id_{i}": gid for i, gid in enumerate(game_ids)}
        where_clause = f"game_id IN ({placeholders})"
    else:
        params = {}
        where_clause = "1=1"

    query = f"""
        SELECT
            game_id,
            home_moneyline,
            away_moneyline,
            home_win_probability,
            away_win_probability,
            spread,
            over_under,
            source,
            fetched_at
        FROM pregame_odds
        WHERE {where_clause}
    """

    return pd.read_sql(text(query), engine, params=params)


def load_odds_snapshots(
    engine: Engine | None = None,
    game_id: str | None = None,
    game_ids: Sequence[str] | None = None,
) -> pd.DataFrame:
    if engine is None:
        engine = get_engine()

    params: dict[str, str] = {}

    if game_id:
        where_clause = "game_id = :game_id"
        params["game_id"] = game_id
    elif game_ids:
        placeholders = ", ".join([f":id_{i}" for i in range(len(game_ids))])
        params = {f"id_{i}": gid for i, gid in enumerate(game_ids)}
        where_clause = f"game_id IN ({placeholders})"
    else:
        where_clause = "1=1"

    query = f"""
        SELECT
            game_id,
            timestamp,
            home_moneyline,
            away_moneyline,
            home_win_probability,
            away_win_probability,
            spread,
            over_under,
            game_clock,
            period,
            home_score,
            away_score
        FROM odds_snapshots
        WHERE {where_clause}
        ORDER BY game_id, timestamp
    """

    return pd.read_sql(text(query), engine, params=params)


def load_games_with_odds(
    engine: Engine | None = None,
    sport: str | None = None,
    status: str | None = None,
    start_date: str | None = None,
    end_date: str | None = None,
    limit: int | None = None,
) -> pd.DataFrame:
    if engine is None:
        engine = get_engine()

    conditions = ["po.game_id IS NOT NULL"]   
    params: dict[str, str | int] = {}

    if sport:
        conditions.append("g.sport = :sport")
        params["sport"] = sport.lower()

    if status:
        conditions.append("g.status = :status")
        params["status"] = status

    if start_date:
        conditions.append("g.scheduled_start >= :start_date")
        params["start_date"] = start_date

    if end_date:
        conditions.append("g.scheduled_start <= :end_date")
        params["end_date"] = end_date

    where_clause = " AND ".join(conditions)
    limit_clause = "LIMIT :limit" if limit is not None else ""
    if limit is not None:
        params["limit"] = limit

    query = f"""
        SELECT
            g.id,
            g.sport,
            g.season_year,
            g.season_type,
            g.status,
            g.home_team,
            g.away_team,
            g.home_team_abbr as home_team_abbreviation,
            g.away_team_abbr as away_team_abbreviation,
            g.home_score,
            g.away_score,
            g.scheduled_start,
            g.actual_start,
            g.end_time,
            po.home_win_probability,
            po.away_win_probability,
            po.spread,
            po.over_under
        FROM games g
        LEFT JOIN pregame_odds po ON g.id = po.game_id
        WHERE {where_clause}
        ORDER BY g.scheduled_start DESC
        {limit_clause}
    """

    return pd.read_sql(text(query), engine, params=params)
