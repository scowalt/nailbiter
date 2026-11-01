
from typing import Tuple

import numpy as np
import pandas as pd
from scipy import stats

from .models import ValidationResult


def get_hype_level(score: float) -> str:
    if score < 40:
        return "low"
    elif score <= 70:
        return "medium"
    else:
        return "high"


def get_excitement_level(score: float) -> str:
    if score < 30:
        return "low"
    elif score <= 60:
        return "medium"
    else:
        return "high"


def compare_hype_vs_excitement(
    hype_scores: pd.DataFrame,
    excitement_scores: pd.DataFrame,
) -> dict:
    merged = pd.merge(
        hype_scores[["game_id", "sport", "hype_score"]],
        excitement_scores[["game_id", "volatility_score"]],
        on="game_id",
        how="inner",
    )

    if len(merged) < 2:
        return {
            "correlation": None,
            "p_value": None,
            "mae": None,
            "rmse": None,
            "n_games": len(merged),
            "merged_data": merged,
        }

    correlation, p_value = stats.pearsonr(merged["hype_score"], merged["volatility_score"])

    errors = merged["hype_score"] - merged["volatility_score"]
    mae = float(np.mean(np.abs(errors)))
    rmse = float(np.sqrt(np.mean(errors**2)))

    merged["hype_level"] = merged["hype_score"].apply(get_hype_level)
    merged["excitement_level"] = merged["volatility_score"].apply(get_excitement_level)
    merged["classification_match"] = merged["hype_level"] == merged["excitement_level"]
    merged["prediction_error"] = errors

    return {
        "correlation": float(correlation),
        "p_value": float(p_value),
        "mae": mae,
        "rmse": rmse,
        "n_games": len(merged),
        "classification_accuracy": float(merged["classification_match"].mean()),
        "merged_data": merged,
    }


def identify_mispredictions(
    comparison_df: pd.DataFrame,
    threshold: float = 30.0,
) -> Tuple[pd.DataFrame, pd.DataFrame]:
    if "prediction_error" not in comparison_df.columns:
        comparison_df = comparison_df.copy()
        comparison_df["prediction_error"] = (
            comparison_df["hype_score"] - comparison_df["volatility_score"]
        )

    over_hyped = comparison_df[comparison_df["prediction_error"] > threshold].copy()
    under_hyped = comparison_df[comparison_df["prediction_error"] < -threshold].copy()

    over_hyped = over_hyped.sort_values("prediction_error", ascending=False)
    under_hyped = under_hyped.sort_values("prediction_error", ascending=True)

    return over_hyped, under_hyped


def calculate_metrics_by_sport(comparison_df: pd.DataFrame) -> pd.DataFrame:
    results = []

    for sport in comparison_df["sport"].unique():
        sport_data = comparison_df[comparison_df["sport"] == sport]

        if len(sport_data) < 2:
            results.append(
                {
                    "sport": sport,
                    "correlation": None,
                    "mae": None,
                    "rmse": None,
                    "classification_accuracy": None,
                    "n_games": len(sport_data),
                }
            )
            continue

        corr, _ = stats.pearsonr(sport_data["hype_score"], sport_data["volatility_score"])
        errors = sport_data["hype_score"] - sport_data["volatility_score"]

        if "classification_match" in sport_data.columns:
            class_acc = float(sport_data["classification_match"].mean())
        else:
            sport_data = sport_data.copy()
            sport_data["hype_level"] = sport_data["hype_score"].apply(get_hype_level)
            sport_data["excitement_level"] = sport_data["volatility_score"].apply(
                get_excitement_level
            )
            class_acc = float((sport_data["hype_level"] == sport_data["excitement_level"]).mean())

        results.append(
            {
                "sport": sport,
                "correlation": float(corr),
                "mae": float(np.mean(np.abs(errors))),
                "rmse": float(np.sqrt(np.mean(errors**2))),
                "classification_accuracy": class_acc,
                "n_games": len(sport_data),
            }
        )

    return pd.DataFrame(results).sort_values("n_games", ascending=False)


def calculate_metrics_by_hype_level(comparison_df: pd.DataFrame) -> pd.DataFrame:
    if "hype_level" not in comparison_df.columns:
        comparison_df = comparison_df.copy()
        comparison_df["hype_level"] = comparison_df["hype_score"].apply(get_hype_level)

    results = []

    for level in ["low", "medium", "high"]:
        level_data = comparison_df[comparison_df["hype_level"] == level]

        if len(level_data) == 0:
            continue

        errors = level_data["hype_score"] - level_data["volatility_score"]

        results.append(
            {
                "hype_level": level,
                "n_games": len(level_data),
                "mean_hype": float(level_data["hype_score"].mean()),
                "mean_excitement": float(level_data["volatility_score"].mean()),
                "mean_error": float(errors.mean()),
                "mae": float(np.mean(np.abs(errors))),
            }
        )

    return pd.DataFrame(results)


def create_validation_results(
    hype_df: pd.DataFrame,
    excitement_df: pd.DataFrame,
) -> list[ValidationResult]:
    merged = pd.merge(
        hype_df[["game_id", "sport", "hype_score"]],
        excitement_df[["game_id", "volatility_score"]],
        on="game_id",
        how="inner",
    )

    results = []
    for _, row in merged.iterrows():
        hype_level = get_hype_level(row["hype_score"])
        excitement_level = get_excitement_level(row["volatility_score"])

        results.append(
            ValidationResult(
                game_id=row["game_id"],
                sport=row["sport"],
                hype_score=row["hype_score"],
                excitement_score=row["volatility_score"],
                prediction_error=row["hype_score"] - row["volatility_score"],
                classification_match=hype_level == excitement_level,
            )
        )

    return results
