# Hype Score Analysis

Jupyter notebooks for exploring hype scores and comparing them with game excitement.

Do not treat current rating snapshots as historical pregame evidence. The database does not retain a qualification history for these calculations. Past-game comparisons leave playoff qualification unavailable. Unknown qualification gives neutral stakes of 50, except postseason games retain maximum stakes of 100. These comparisons cannot validate or tune historical playoff stakes.

## Setup

```bash
# Install uv if you haven't already
curl -LsSf https://astral.sh/uv/install.sh | sh

# Create virtual environment and install dependencies
cd apps/analysis
uv sync

# Start JupyterLab
uv run jupyter lab
```

## Database Connection

The notebooks connect to the same PostgreSQL/TimescaleDB database as the backend.

For local development, ensure the database is running:

```bash
# From repo root
docker compose up -d timescaledb
```

Default connection: `postgresql://postgres:postgres@localhost:5432/excitement_score`

Set `DATABASE_URL` environment variable to override.

## Notebooks

| Notebook | Purpose |
| --- | --- |
| `01_data_exploration.ipynb` | Explore historical game data |
| `02_hype_score_calculation.ipynb` | Calculate hype scores with adjustable parameters |
| `03_excitement_analysis.ipynb` | Analyze actual game excitement from odds volatility |
| `04_validation_comparison.ipynb` | Compare predicted hype vs actual excitement |
| `05_parameter_tuning.ipynb` | Interactive parameter tuning with widgets |

## Module Structure

```text
src/hype_score/
├── config.py      # Parameter configuration (weights, normalization)
├── database.py    # PostgreSQL connection utilities
├── models.py      # Pydantic models for data validation
├── ratings.py     # Season-specific Kalshi qualification policy
├── scoring.py     # Hype score calculation (ported from C#)
├── excitement.py  # Actual excitement from odds volatility
└── validation.py  # Compare predicted vs actual
```

## Playoff probability policy

Playoff probability means the chance that a team qualifies for its season's playoffs. It does not mean championship probability or team strength.

The legacy NHL PlayoffStatus model fallback is **rejected**, not awaiting permission or activation ([ADR 0002](../../docs/adr/0002-nhl-market-only-qualification.md)). [ADR 0003](../../docs/adr/0003-nhl-moneypuck-qualification-exception.md) permits the specifically approved MoneyPuck NHL runtime forecast exception. Its application defaults keep collection and selection off; production overrides and remaining activation gates are documented in the [MoneyPuck runbook](../../docs/nhl-moneypuck-qualification.md). This analysis package deliberately remains **Kalshi-only**: it does not reproduce runtime model fallbacks, shadow-evaluate them, or use current model rows as historical pregame evidence. Polymarket rows remain rejected and cannot fill missing qualification here. Current snapshots still cannot establish past pregame evidence.

For NFL, NBA, NHL, and MLB, qualification comes only from Kalshi. The loader first selects the newest Kalshi row for each `(sport, team_abbreviation, season)` key. It then applies these rules:

- Require `source_season == season` and a finite probability between zero and one.
- Reject a missing or future `fetched_at` timestamp.
- Require an age below one hour when `playoff_probability_is_final` is false.
- Accept final probabilities only when their value is zero or one. These final values do not expire.
- Keep a latest null or rejected value unavailable. Never substitute an older quote or another provider.

The database must include `source_season` and `playoff_probability_is_final BOOLEAN NOT NULL DEFAULT false`. The backend migration supplies these columns. Strength and championship selection remain separate from qualification, within the same team and season. The output retains teams without qualification or strength data.

## Evaluation time and seasons

`load_team_ratings`, `load_team_ratings_for_games`, `calculate_hype_score`, and `calculate_hype_scores_batch` require `evaluated_at`. This value must include a time zone. The direct `calculate_stakes` function also requires the expected `season`.

Use the game's stored `season_year`. NFL uses the regular season's starting year, NBA and NHL use the ending year, and MLB uses the calendar year. Missing game seasons stay unavailable instead of using the current year. SQL filters use bound parameters.

```python
import pandas as pd
from hype_score.database import load_team_ratings_for_games
from hype_score.scoring import calculate_hype_scores_batch

evaluated_at = pd.Timestamp.now(tz="UTC")
ratings = load_team_ratings_for_games(engine, games, evaluated_at=evaluated_at)
scores = calculate_hype_scores_batch(
    games, pregame_odds, ratings, evaluated_at=evaluated_at
)
```

The game loader returns `season_year` and `season_type`. The ratings helper queries each requested sport and season separately. Batch scoring requires one selected row per team and season and rejects duplicate keys. Games with pregame odds remain in the output even without ratings.

Scoring assesses qualification again at `evaluated_at`. It uses qualification only for a scheduled game whose start time is later than that time. It leaves qualification unavailable for past, started, or completed games, including when a current snapshot contains a final result. Setting an earlier evaluation time does not make a later fetch valid evidence.

Unavailable qualification stays `None` in model fields and missing in DataFrames. If either team lacks qualification evidence, stakes equal 50. In single-game and batch scoring, `season_type == 3` instead gives maximum stakes of 100. This postseason rule uses game metadata and does not supply a missing qualification probability. The existing closeness calculation still uses its numeric 0.5 fallback internally. That fallback does not become a displayed probability.

The notebooks use this shared path and an explicit evaluation time. Saved outputs from the old probability sources are cleared. Notebook `06_regression_tests.ipynb` uses fixed synthetic fixtures, not observed markets.

Strength and championship data still lack a historical evidence guarantee. The calculations are not a full point-in-time backtest, which requires inputs available before each game. These changes do not reconstruct historical inputs, import old markets, or tune the algorithm. Historical prediction claims require a separate archive with evidence of what was known before each game.

## Isolated tests

With the local project dependencies available, run this command from `apps/analysis`:

```bash
PYTHONPATH=src python -m unittest discover -s tests -v
```

The tests use in-memory SQLite, synthetic rows, and a fixed clock. They do not read database credentials or contact external services. They cover provider and season selection, expiry, final results, latest-null behavior, notebook calls, and scoring alignment. Full data notebooks and PostgreSQL deployment remain separate verification steps.

## Configuration

Default weights are in `configs/default_weights.toml`. You can:

1. Edit the TOML file for persistent changes
2. Use ipywidgets sliders in notebooks for interactive tuning
3. Override programmatically when calling scoring functions
