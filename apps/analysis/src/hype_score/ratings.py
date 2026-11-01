
from collections.abc import Mapping
from datetime import datetime, timedelta
from math import isfinite
from typing import Any

import pandas as pd

SUPPORTED_PLAYOFF_SPORTS = frozenset({"nfl", "nba", "nhl", "mlb"})
RATING_KEYS = ["sport", "team_abbreviation", "season"]
RATING_COLUMNS = RATING_KEYS + [
    "elo_rating",
    "bpi_rating",
    "fpi_rating",
    "championship_probability",
    "championship_source",
    "championship_fetched_at",
    "playoff_probability",
    "playoff_probability_is_final",
    "source",
    "source_season",
    "fetched_at",
]


def evaluation_time(value: datetime) -> pd.Timestamp:
    timestamp = pd.Timestamp(value)
    if pd.isna(timestamp) or timestamp.tzinfo is None:
        raise ValueError("evaluated_at must be a timezone-aware datetime")
    return timestamp.tz_convert("UTC")


def optional_value(value: Any) -> Any:
    return None if pd.isna(value) else value


def playoff_probability(
    row: Mapping[str, Any], *, sport: str, season: int | None, evaluated_at: datetime
) -> float | None:
    now = evaluation_time(evaluated_at)
    if (
        season is None
        or sport not in SUPPORTED_PLAYOFF_SPORTS
        or row.get("sport") != sport
        or row.get("source") != "kalshi"
        or optional_value(row.get("season")) != season
        or optional_value(row.get("source_season")) != season
    ):
        return None
    value = optional_value(row.get("playoff_probability"))
    fetched_at = pd.to_datetime(row.get("fetched_at"), utc=True, errors="coerce")
    if value is None or pd.isna(fetched_at) or fetched_at > now:
        return None
    try:
        probability = float(value)
    except (TypeError, ValueError):
        return None
    if not isfinite(probability) or not 0.0 <= probability <= 1.0:
        return None
    is_final = optional_value(row.get("playoff_probability_is_final", False))
    if is_final not in (False, True):
        return None
    if is_final:
        return probability if probability in (0.0, 1.0) else None
    return probability if now - fetched_at < timedelta(hours=1) else None


def select_team_ratings(rows: pd.DataFrame, *, evaluated_at: datetime) -> pd.DataFrame:
    now = evaluation_time(evaluated_at)
    if rows.empty:
        return pd.DataFrame(columns=RATING_COLUMNS)
    rows = rows.copy()
    rows["fetched_at"] = pd.to_datetime(
        rows["fetched_at"], utc=True, errors="coerce", format="mixed"
    )
    order = ["fetched_at"] + (["id"] if "id" in rows.columns else [])
    rows = rows.sort_values(order, ascending=False, na_position="first", kind="stable")
    selected = []
    for key, group in rows.groupby(RATING_KEYS, sort=False, dropna=False):
        result = dict.fromkeys(RATING_COLUMNS)
        result.update(zip(RATING_KEYS, key))
        for field in ("elo_rating", "bpi_rating", "fpi_rating", "championship_probability"):
            available = group[group[field].notna()]
            if not available.empty:
                latest = available.iloc[0]
                result[field] = latest[field]
                if field == "championship_probability":
                    result["championship_source"] = latest["source"]
                    result["championship_fetched_at"] = latest["fetched_at"]
        kalshi = group[group["source"] == "kalshi"]
        result["playoff_probability_is_final"] = False
        if not kalshi.empty:
            latest = kalshi.iloc[0]
            for field in ("source", "source_season", "fetched_at"):
                result[field] = optional_value(latest.get(field))
            probability = playoff_probability(
                latest, sport=key[0], season=optional_value(key[2]), evaluated_at=now
            )
            result["playoff_probability"] = probability
            result["playoff_probability_is_final"] = probability is not None and bool(
                latest.get("playoff_probability_is_final", False)
            )
        selected.append(result)
    return pd.DataFrame(selected, columns=RATING_COLUMNS)
