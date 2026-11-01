
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

try:
    import tomllib
except ImportError:
    import tomli as tomllib  # type: ignore[import-not-found]


@dataclass
class SportWeights:

    stakes_weight: float
    quality_weight: float
    closeness_weight: float

    def __post_init__(self) -> None:
        total = self.stakes_weight + self.quality_weight + self.closeness_weight
        if abs(total - 1.0) > 0.01:
            raise ValueError(f"Weights must sum to 1.0, got {total}")


@dataclass
class NormalizationConfig:

    elo_min: float = 1300.0
    elo_max: float = 1700.0

    bpi_min: float = -20.0
    bpi_max: float = 20.0

    nhl_points_min: float = 60.0
    nhl_points_max: float = 130.0


@dataclass
class StakesConfig:

    bubble_factor_max: float = 40.0

    contender_factor_max: float = 30.0

    champ_bonus_weight: float = 0.2

    season_progress_max: float = 20.0


@dataclass
class SeasonDates:

    nfl_start: tuple[int, int] = (9, 1)   
    nfl_end: tuple[int, int] = (2, 15)   

    nba_start: tuple[int, int] = (10, 15)   
    nba_end: tuple[int, int] = (4, 15)   

    nhl_start: tuple[int, int] = (10, 1)   
    nhl_end: tuple[int, int] = (4, 15)   

    mlb_start: tuple[int, int] = (3, 28)   
    mlb_end: tuple[int, int] = (10, 1)   



@dataclass
class SeasonGamesConfig:

    nfl: int = 17
    nba: int = 82
    nhl: int = 82
    mlb: int = 162

    def get_total(self, sport: str) -> int:
        return getattr(self, sport.lower(), 82)


@dataclass
class UrgencyConfig:

    steepness: float = 3.0


@dataclass
class HypeConfig:

    sport_weights: dict[str, SportWeights] = field(default_factory=dict)
    normalization: NormalizationConfig = field(default_factory=NormalizationConfig)
    stakes: StakesConfig = field(default_factory=StakesConfig)
    season_dates: SeasonDates = field(default_factory=SeasonDates)
    season_games: SeasonGamesConfig = field(default_factory=SeasonGamesConfig)
    urgency: UrgencyConfig = field(default_factory=UrgencyConfig)

    @classmethod
    def default(cls) -> "HypeConfig":
        return cls(
            sport_weights={
                "nfl": SportWeights(0.45, 0.35, 0.20),   
                "nba": SportWeights(0.30, 0.40, 0.30),   
                "mlb": SportWeights(0.35, 0.30, 0.35),   
                "nhl": SportWeights(0.35, 0.35, 0.30),   
            },
            normalization=NormalizationConfig(),
            stakes=StakesConfig(),
            season_dates=SeasonDates(),
            season_games=SeasonGamesConfig(),
            urgency=UrgencyConfig(),
        )

    @classmethod
    def from_toml(cls, path: Path) -> "HypeConfig":
        with open(path, "rb") as f:
            data = tomllib.load(f)

        config = cls.default()

        if "sport_weights" in data:
            for sport, weights in data["sport_weights"].items():
                config.sport_weights[sport.lower()] = SportWeights(
                    stakes_weight=weights.get("stakes", 0.33),
                    quality_weight=weights.get("quality", 0.33),
                    closeness_weight=weights.get("closeness", 0.34),
                )

        if "normalization" in data:
            norm = data["normalization"]
            config.normalization = NormalizationConfig(
                elo_min=norm.get("elo_min", config.normalization.elo_min),
                elo_max=norm.get("elo_max", config.normalization.elo_max),
                bpi_min=norm.get("bpi_min", config.normalization.bpi_min),
                bpi_max=norm.get("bpi_max", config.normalization.bpi_max),
                nhl_points_min=norm.get("nhl_points_min", config.normalization.nhl_points_min),
                nhl_points_max=norm.get("nhl_points_max", config.normalization.nhl_points_max),
            )

        if "stakes" in data:
            stakes = data["stakes"]
            config.stakes = StakesConfig(
                bubble_factor_max=stakes.get("bubble_factor_max", config.stakes.bubble_factor_max),
                champ_bonus_weight=stakes.get("champ_bonus_weight", config.stakes.champ_bonus_weight),
                season_progress_max=stakes.get(
                    "season_progress_max", config.stakes.season_progress_max
                ),
            )

        return config

    def to_dict(self) -> dict[str, Any]:
        return {
            "sport_weights": {
                sport: {
                    "stakes": w.stakes_weight,
                    "quality": w.quality_weight,
                    "closeness": w.closeness_weight,
                }
                for sport, w in self.sport_weights.items()
            },
            "normalization": {
                "elo_min": self.normalization.elo_min,
                "elo_max": self.normalization.elo_max,
                "bpi_min": self.normalization.bpi_min,
                "bpi_max": self.normalization.bpi_max,
                "nhl_points_min": self.normalization.nhl_points_min,
                "nhl_points_max": self.normalization.nhl_points_max,
            },
            "stakes": {
                "bubble_factor_max": self.stakes.bubble_factor_max,
                "champ_bonus_weight": self.stakes.champ_bonus_weight,
                "season_progress_max": self.stakes.season_progress_max,
            },
        }

    def get_sport_weights(self, sport: str) -> SportWeights:
        return self.sport_weights.get(
            sport.lower(),
            SportWeights(0.33, 0.33, 0.34),   
        )
