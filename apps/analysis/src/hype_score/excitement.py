
import pandas as pd
import numpy as np

from .models import ExcitementResult


def calculate_odds_volatility(snapshots: pd.DataFrame) -> float:
    if len(snapshots) < 2:
        return 0.0

    snapshots = snapshots.sort_values("timestamp")

    prob_changes = snapshots["home_win_probability"].diff().abs()

    total_movement = prob_changes.sum()

    return min(100.0, total_movement * 50)


def calculate_lead_changes(snapshots: pd.DataFrame) -> int:
    if len(snapshots) < 2:
        return 0

    snapshots = snapshots.sort_values("timestamp")
    probs = snapshots["home_win_probability"]

    above_50 = probs > 0.5
    changes = (above_50 != above_50.shift()).sum()

    return max(0, int(changes) - 1)


def calculate_finish_drama(snapshots: pd.DataFrame) -> float:
    if len(snapshots) < 2:
        return 0.0

    snapshots = snapshots.sort_values("timestamp")

    n = len(snapshots)
    final_count = max(1, n // 10)
    final_snapshots = snapshots.tail(final_count)

    final_probs = final_snapshots["home_win_probability"]
    closeness_scores = (1 - np.abs(final_probs - 0.5) * 2) * 100

    return float(closeness_scores.mean())


def calculate_max_comeback(snapshots: pd.DataFrame) -> float:
    if len(snapshots) < 2:
        return 0.0

    snapshots = snapshots.sort_values("timestamp")
    probs = snapshots["home_win_probability"]

    home_min = probs.min()
    away_min = (1 - probs).min()   

    final_home = probs.iloc[-1]
    final_away = 1 - final_home

    home_comeback = max(0, final_home - home_min) if final_home > 0.5 else 0

    away_comeback = max(0, final_away - away_min) if final_away > 0.5 else 0

    return max(home_comeback, away_comeback) * 100


def calculate_excitement(snapshots: pd.DataFrame) -> ExcitementResult:
    if len(snapshots) == 0:
        raise ValueError("No snapshots provided")

    game_id = str(snapshots.iloc[0]["game_id"])

    return ExcitementResult(
        game_id=game_id,
        volatility_score=calculate_odds_volatility(snapshots),
        finish_drama_score=calculate_finish_drama(snapshots),
        total_prob_movement=float(snapshots["home_win_probability"].diff().abs().sum()),
        max_comeback=calculate_max_comeback(snapshots),
        lead_changes=calculate_lead_changes(snapshots),
        snapshot_count=len(snapshots),
    )


def calculate_excitement_batch(
    all_snapshots: pd.DataFrame,
) -> pd.DataFrame:
    results = []

    for game_id in all_snapshots["game_id"].unique():
        game_snapshots = all_snapshots[all_snapshots["game_id"] == game_id]

        try:
            result = calculate_excitement(game_snapshots)
            results.append(
                {
                    "game_id": result.game_id,
                    "volatility_score": result.volatility_score,
                    "finish_drama_score": result.finish_drama_score,
                    "total_prob_movement": result.total_prob_movement,
                    "max_comeback": result.max_comeback,
                    "lead_changes": result.lead_changes,
                    "snapshot_count": result.snapshot_count,
                }
            )
        except ValueError:
            continue

    return pd.DataFrame(results)


def get_excitement_level(score: float) -> str:
    if score < 30:
        return "low"
    elif score <= 60:
        return "medium"
    else:
        return "high"
