
from datetime import datetime
from typing import Optional

from pydantic import BaseModel, Field


class Game(BaseModel):
    __doc__ = 'Game data model matching database schema.'

    id: str
    sport: str
    status: str
    home_team: str
    away_team: str
    home_team_abbreviation: str
    away_team_abbreviation: str
    scheduled_start: datetime
    season_year: Optional[int] = None
    season_type: Optional[int] = None
    actual_start: Optional[datetime] = None
    end_time: Optional[datetime] = None
    home_score: Optional[int] = None
    away_score: Optional[int] = None


class TeamRating(BaseModel):
    __doc__ = 'Team rating data model.'

    sport: str
    team_abbreviation: str
    season: int
    elo_rating: Optional[float] = None
    bpi_rating: Optional[float] = None
    fpi_rating: Optional[float] = None
    playoff_probability: Optional[float] = None
    playoff_probability_is_final: bool = False
    championship_probability: Optional[float] = None
    source: Optional[str] = None
    source_season: Optional[int] = None
    fetched_at: Optional[datetime] = None

    @property
    def rating(self) -> Optional[float]:
        return self.elo_rating or self.bpi_rating or self.fpi_rating


class TeamStanding(BaseModel):
    __doc__ = 'Team standing data model with win/loss record.'

    sport: str
    team_abbreviation: str
    season: int
    division: Optional[str] = None
    conference: Optional[str] = None
    division_rank: Optional[int] = None
    conference_rank: Optional[int] = None
    national_rank: Optional[int] = None
    wins: Optional[int] = None
    losses: Optional[int] = None
    ties: Optional[int] = None
    fetched_at: Optional[datetime] = None

    @property
    def games_played(self) -> int:
        return (self.wins or 0) + (self.losses or 0) + (self.ties or 0)


class PregameOdds(BaseModel):
    __doc__ = 'Pregame odds data model.'

    game_id: str
    home_win_probability: float
    away_win_probability: float
    home_moneyline: Optional[float] = None
    away_moneyline: Optional[float] = None
    spread: Optional[float] = None
    over_under: Optional[float] = None
    source: Optional[str] = None
    fetched_at: Optional[datetime] = None


class OddsSnapshot(BaseModel):
    __doc__ = 'Live odds snapshot data model.'

    game_id: str
    timestamp: datetime
    home_win_probability: float
    away_win_probability: float
    home_moneyline: Optional[float] = None
    away_moneyline: Optional[float] = None
    spread: Optional[float] = None
    over_under: Optional[float] = None
    game_clock: Optional[str] = None
    period: Optional[int] = None
    home_score: Optional[int] = None
    away_score: Optional[int] = None


class HypeComponents(BaseModel):
    __doc__ = 'Breakdown of hype score components.'

    stakes_score: float = Field(ge=0, le=100)
    quality_score: float = Field(ge=0, le=100)
    closeness_score: float = Field(ge=0, le=100)

    home_team_rating: Optional[float] = None
    away_team_rating: Optional[float] = None
    home_playoff_odds: Optional[float] = None
    away_playoff_odds: Optional[float] = None


class HypeResult(BaseModel):
    __doc__ = 'Complete hype score result for a game.'

    game_id: str
    sport: str
    hype_score: float = Field(ge=0, le=100)
    components: HypeComponents


class ExcitementResult(BaseModel):
    __doc__ = 'Actual excitement metrics calculated from live odds.'

    game_id: str
    volatility_score: float = Field(ge=0, le=100)
    finish_drama_score: float = Field(ge=0, le=100)
    total_prob_movement: float = Field(ge=0)
    max_comeback: float = Field(ge=0, le=100)
    lead_changes: int = Field(ge=0)
    snapshot_count: int = Field(ge=0)


class ValidationResult(BaseModel):
    __doc__ = 'Result of comparing hype predictions to actual excitement.'

    game_id: str
    sport: str
    hype_score: float
    excitement_score: float
    prediction_error: float   
    classification_match: bool   
