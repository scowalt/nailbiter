
import math
from datetime import datetime
from typing import Optional

import pandas as pd

from .config import HypeConfig
from .models import Game, HypeComponents, HypeResult, TeamRating, TeamStanding, PregameOdds
from .ratings import evaluation_time, optional_value, playoff_probability


def _rating_for_game(
    rating: Optional[TeamRating], game: Game, team: str, evaluated_at: datetime
) -> Optional[TeamRating]:
    if rating is None or (
        rating.sport != game.sport.lower()
        or rating.team_abbreviation != team
        or game.season_year is None
        or rating.season != game.season_year
    ):
        return None
    probability = None
    if game.status == "scheduled" and pd.to_datetime(game.scheduled_start, utc=True) > evaluated_at:
        probability = playoff_probability(
            rating.model_dump(), sport=game.sport.lower(), season=game.season_year,
            evaluated_at=evaluated_at,
        )
    return rating.model_copy(update={
        "playoff_probability": probability,
        "playoff_probability_is_final": probability is not None and rating.playoff_probability_is_final,
    })


def calculate_closeness(home_win_probability: float) -> float:
    deviation = abs(home_win_probability - 0.5)
    return (1 - (deviation * 2)) * 100


def calculate_quality(
    home_rating: Optional[float],
    away_rating: Optional[float],
    sport: str,
    config: HypeConfig,
) -> float:
    if home_rating is None or away_rating is None:
        return 50.0   

    norm = config.normalization

    if sport.lower() == "mlb":
        normalized_home = max(0, min(1, (home_rating - norm.elo_min) / (norm.elo_max - norm.elo_min)))
        normalized_away = max(0, min(1, (away_rating - norm.elo_min) / (norm.elo_max - norm.elo_min)))
    elif sport.lower() == "nhl":
        normalized_home = max(
            0, min(1, (home_rating - norm.nhl_points_min) / (norm.nhl_points_max - norm.nhl_points_min))
        )
        normalized_away = max(
            0, min(1, (away_rating - norm.nhl_points_min) / (norm.nhl_points_max - norm.nhl_points_min))
        )
    else:
        normalized_home = max(0, min(1, (home_rating - norm.bpi_min) / (norm.bpi_max - norm.bpi_min)))
        normalized_away = max(0, min(1, (away_rating - norm.bpi_min) / (norm.bpi_max - norm.bpi_min)))

    if normalized_home + normalized_away < 0.001:
        return 0.0

    harmonic_mean = 2 * normalized_home * normalized_away / (normalized_home + normalized_away)
    return harmonic_mean * 100


def get_season_progress(sport: str, game_date: datetime, config: HypeConfig) -> float:
    if hasattr(game_date, 'tzinfo') and game_date.tzinfo is not None:
        game_date = game_date.replace(tzinfo=None)

    dates = config.season_dates
    year = game_date.year

    sport_lower = sport.lower()
    if sport_lower == "nfl":
        start = datetime(year, dates.nfl_start[0], dates.nfl_start[1])
        end = datetime(year + 1, dates.nfl_end[0], dates.nfl_end[1])
    elif sport_lower == "nba":
        start = datetime(year, dates.nba_start[0], dates.nba_start[1])
        end = datetime(year + 1, dates.nba_end[0], dates.nba_end[1])
    elif sport_lower == "nhl":
        start = datetime(year, dates.nhl_start[0], dates.nhl_start[1])
        end = datetime(year + 1, dates.nhl_end[0], dates.nhl_end[1])
    elif sport_lower == "mlb":
        start = datetime(year, dates.mlb_start[0], dates.mlb_start[1])
        end = datetime(year, dates.mlb_end[0], dates.mlb_end[1])
    else:
        start = game_date.replace(month=max(1, game_date.month - 3))
        end = game_date.replace(month=min(12, game_date.month + 3))

    if game_date < start and sport_lower != "mlb":
        start = start.replace(year=start.year - 1)
        end = end.replace(year=end.year - 1)

    total_days = (end - start).days
    elapsed_days = (game_date - start).days

    if total_days <= 0:
        return 0.0

    return max(0.0, min(1.0, elapsed_days / total_days))


def calculate_playoff_urgency(
    games_remaining: int,
    total_games: int,
    config: HypeConfig,
) -> float:
    if total_games <= 0:
        return 1.0

    games_played = total_games - games_remaining
    progress = max(0.0, min(1.0, games_played / total_games))

    k = config.urgency.steepness
    urgency = (math.exp(k * progress) - 1) / (math.exp(k) - 1)

    return max(0.0, min(1.0, urgency))


def get_games_remaining(
    standing: Optional[TeamStanding],
    sport: str,
    game_date: datetime,
    config: HypeConfig,
) -> int:
    total_games = config.season_games.get_total(sport)

    if standing is not None and standing.games_played > 0:
        return max(0, total_games - standing.games_played)

    progress = get_season_progress(sport, game_date, config)
    return max(0, int(total_games * (1 - progress)))


def calculate_stakes(
    game_date: datetime,
    sport: str,
    home_rating: Optional[TeamRating],
    away_rating: Optional[TeamRating],
    config: HypeConfig,
    home_standing: Optional[TeamStanding] = None,
    away_standing: Optional[TeamStanding] = None,
    *,
    season: int | None,
    evaluated_at: datetime,
) -> float:
    now = evaluation_time(evaluated_at)
    if pd.to_datetime(game_date, utc=True) <= now:
        return 50.0
    home_playoff = playoff_probability(
        home_rating.model_dump() if home_rating else {},
        sport=sport.lower(), season=season, evaluated_at=now,
    )
    away_playoff = playoff_probability(
        away_rating.model_dump() if away_rating else {},
        sport=sport.lower(), season=season, evaluated_at=now,
    )
    if home_playoff is None or away_playoff is None:
        return 50.0
    home_champ = home_rating.championship_probability if home_rating else 0.0
    away_champ = away_rating.championship_probability if away_rating else 0.0

    if home_champ is None:
        home_champ = 0.0
    if away_champ is None:
        away_champ = 0.0

    home_bubble = 1 - abs(home_playoff - 0.5) * 2
    away_bubble = 1 - abs(away_playoff - 0.5) * 2

    contender_factor = min(home_playoff, away_playoff)

    champ_bonus = (home_champ + away_champ) * 100

    total_games = config.season_games.get_total(sport)
    home_games_remaining = get_games_remaining(home_standing, sport, game_date, config)
    away_games_remaining = get_games_remaining(away_standing, sport, game_date, config)
    avg_games_remaining = (home_games_remaining + away_games_remaining) // 2

    urgency = calculate_playoff_urgency(avg_games_remaining, total_games, config)

    stakes_config = config.stakes
    urgency_scaled_bubble = ((home_bubble + away_bubble) / 2 * stakes_config.bubble_factor_max) * urgency
    urgency_scaled_contender = (contender_factor * stakes_config.contender_factor_max) * urgency
    urgency_scaled_champ = (champ_bonus * stakes_config.champ_bonus_weight) * urgency

    season_progress = get_season_progress(sport, game_date, config)
    late_season_bonus = season_progress * stakes_config.season_progress_max

    stakes = (
        urgency_scaled_bubble +       
        urgency_scaled_contender +    
        urgency_scaled_champ +        
        late_season_bonus             
    )

    return max(0.0, min(100.0, stakes))


def calculate_hype_score(
    game: Game,
    pregame_odds: PregameOdds,
    home_rating: Optional[TeamRating],
    away_rating: Optional[TeamRating],
    config: Optional[HypeConfig] = None,
    *,
    evaluated_at: datetime,
) -> HypeResult:
    if config is None:
        config = HypeConfig.default()

    now = evaluation_time(evaluated_at)
    home_rating = _rating_for_game(home_rating, game, game.home_team_abbreviation, now)
    away_rating = _rating_for_game(away_rating, game, game.away_team_abbreviation, now)

    closeness = calculate_closeness(pregame_odds.home_win_probability)

    home_rating_value = home_rating.rating if home_rating else None
    away_rating_value = away_rating.rating if away_rating else None

    quality = calculate_quality(home_rating_value, away_rating_value, game.sport, config)

    stakes = 100.0 if game.season_type == 3 else calculate_stakes(
        game.scheduled_start, game.sport, home_rating, away_rating, config,
        season=game.season_year, evaluated_at=now,
    )

    home_playoff = home_rating.playoff_probability if home_rating else 0.5
    away_playoff = away_rating.playoff_probability if away_rating else 0.5
    if home_playoff is None:
        home_playoff = 0.5
    if away_playoff is None:
        away_playoff = 0.5

    home_bubble = 1 - abs(home_playoff - 0.5) * 2
    away_bubble = 1 - abs(away_playoff - 0.5) * 2
    bubble_relevance = (home_bubble + away_bubble) / 2

    contender_relevance = min(home_playoff, away_playoff)

    relevance = max(bubble_relevance, contender_relevance)

    effective_closeness = closeness * relevance

    weights = config.get_sport_weights(game.sport)

    final_score = (
        (stakes * weights.stakes_weight)
        + (quality * weights.quality_weight)
        + (effective_closeness * weights.closeness_weight)
    )
    final_score = max(0.0, min(100.0, final_score))

    components = HypeComponents(
        stakes_score=stakes,
        quality_score=quality,
        closeness_score=closeness,
        home_team_rating=home_rating_value,
        away_team_rating=away_rating_value,
        home_playoff_odds=home_rating.playoff_probability if home_rating else None,
        away_playoff_odds=away_rating.playoff_probability if away_rating else None,
    )

    return HypeResult(
        game_id=game.id,
        sport=game.sport,
        hype_score=final_score,
        components=components,
    )


def calculate_hype_scores_batch(
    games_df: pd.DataFrame,
    pregame_odds_df: pd.DataFrame,
    ratings_df: pd.DataFrame,
    config: Optional[HypeConfig] = None,
    *,
    evaluated_at: datetime,
) -> pd.DataFrame:
    if config is None:
        config = HypeConfig.default()

    now = evaluation_time(evaluated_at)
    results = []

    odds_by_game = {row.game_id: row for row in pregame_odds_df.itertuples()}

    ratings_lookup: dict[tuple[str, str, int], pd.Series] = {}
    for _, row in ratings_df.iterrows():
        season = optional_value(row.get("season"))
        if season is None:
            continue
        key = (row["sport"].lower(), row["team_abbreviation"], int(season))
        if key in ratings_lookup:
            raise ValueError("Duplicate team/season ratings; use load_team_ratings first")
        ratings_lookup[key] = row

    for _, game_row in games_df.iterrows():
        game_id = game_row["id"]

        if game_id not in odds_by_game:
            continue

        odds_row = odds_by_game[game_id]

        sport = game_row["sport"].lower()
        season = optional_value(game_row.get("season_year"))
        home_key = (sport, game_row["home_team_abbreviation"], season)
        away_key = (sport, game_row["away_team_abbreviation"], season)

        home_rating_row = ratings_lookup.get(home_key)
        away_rating_row = ratings_lookup.get(away_key)

        game = Game(
            id=game_id,
            sport=game_row["sport"],
            status=game_row["status"],
            home_team=game_row["home_team"],
            away_team=game_row["away_team"],
            home_team_abbreviation=game_row["home_team_abbreviation"],
            away_team_abbreviation=game_row["away_team_abbreviation"],
            scheduled_start=pd.to_datetime(game_row["scheduled_start"], utc=True),
            season_year=season,
            season_type=optional_value(game_row.get("season_type")),
            home_score=optional_value(game_row.get("home_score")),
            away_score=optional_value(game_row.get("away_score")),
        )

        pregame_odds = PregameOdds(
            game_id=game_id,
            home_win_probability=odds_row.home_win_probability,
            away_win_probability=odds_row.away_win_probability,
        )

        def rating_model(row: pd.Series | None) -> TeamRating | None:
            if row is None:
                return None
            return TeamRating(**{
                field: optional_value(row[field])
                for field in TeamRating.model_fields if field in row
            })

        result = calculate_hype_score(
            game, pregame_odds, rating_model(home_rating_row), rating_model(away_rating_row),
            config, evaluated_at=now,
        )

        results.append(
            {
                "game_id": result.game_id,
                "sport": result.sport,
                "season_year": game.season_year,
                "evaluated_at": now,
                "hype_score": result.hype_score,
                "stakes_score": result.components.stakes_score,
                "quality_score": result.components.quality_score,
                "closeness_score": result.components.closeness_score,
                "home_team": game_row["home_team"],
                "away_team": game_row["away_team"],
                "home_team_rating": result.components.home_team_rating,
                "away_team_rating": result.components.away_team_rating,
                "home_playoff_odds": result.components.home_playoff_odds,
                "away_playoff_odds": result.components.away_playoff_odds,
                "scheduled_start": game_row["scheduled_start"],
            }
        )

    return pd.DataFrame(results)
