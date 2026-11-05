import math
from statistics import NormalDist

import numpy as np

LEAGUES = {"nfl": (32, 17, 1.05, 7), "nba": (30, 82, 0.85, 8), "nhl": (32, 82, 0.40, 8), "mlb": (30, 162, 0.32, 6)}
PROGRESS = (0.3, 0.5, 0.7, 0.8, 0.9, 0.95)
SEASONS_PER_CHECKPOINT = 4
SIMULATIONS = 3000


def play_returning_final_wins_and_first_round_results(strength, wins, rounds, rng):
    sims, teams = wins.shape
    first = None
    for round_number in range(rounds):
        order = np.argsort(rng.random((sims, teams)), axis=1)
        home, away = order[:, : teams // 2], order[:, teams // 2 :]
        home_won = rng.random(home.shape) < 1 / (1 + np.exp(strength[away] - strength[home]))
        won = np.zeros((sims, teams), bool)
        np.put_along_axis(won, home, home_won, 1)
        np.put_along_axis(won, away, ~home_won, 1)
        wins = wins + won
        if round_number == 0:
            first = won
    return wins, first


def qualified(wins, conference, qualifiers, rng):
    jittered = wins + rng.random(wins.shape) * 1e-3
    result = np.zeros(wins.shape, bool)
    for side in (0, 1):
        members = np.flatnonzero(conference == side)
        top = np.argsort(-jittered[:, members], axis=1)[:, :qualifiers]
        chosen = np.zeros((wins.shape[0], len(members)), bool)
        np.put_along_axis(chosen, top, True, 1)
        result[:, members] = chosen
    return result


def bubble_team_swings(rng):
    for league, (teams, length, spread, qualifiers) in LEAGUES.items():
        for progress in PROGRESS:
            for _ in range(SEASONS_PER_CHECKPOINT):
                strength = rng.normal(0, spread, teams)
                conference = np.arange(teams) % 2
                completed = min(round(progress * length), length - 1)
                played, _ = play_returning_final_wins_and_first_round_results(strength, np.zeros((1, teams)), completed, rng)
                final, first = play_returning_final_wins_and_first_round_results(strength, np.repeat(played, SIMULATIONS, 0), length - completed, rng)
                made = qualified(final, conference, qualifiers, rng)
                for team, probability in enumerate(made.mean(0)):
                    if 0.03 < probability < 0.97:
                        won = first[:, team]
                        swing = made[won, team].mean() - made[~won, team].mean()
                        yield league, length - completed, probability, max(swing, 0.0)


def estimate(probability, remaining, game, cut_line):
    z = NormalDist().inv_cdf(probability)
    return math.exp(-z * z / 2) / math.sqrt(2 * math.pi) / math.sqrt(game**2 * (remaining - 1) + cut_line**2)


def main():
    rows = list(bubble_team_swings(np.random.default_rng(7)))
    grid = [(game, cut_line) for game in np.arange(0.40, 1.001, 0.05) for cut_line in np.arange(0.40, 2.001, 0.10)]
    game, cut_line = min(grid, key=lambda c: sum((estimate(p, r, *c) - s) ** 2 for _, r, p, s in rows))
    print(f"fit: GameSpreadInWins = {game:.2f}, CutLineSpreadInWins = {cut_line:.1f}")
    for league in LEAGUES:
        subset = [(r, p, s) for name, r, p, s in rows if name == league]
        truth = np.array([s for _, _, s in subset])
        fitted = np.array([estimate(p, r, game, cut_line) for r, p, _ in subset])
        print(f"{league}: n={len(subset)} true Stakes mean {80 * truth.mean():5.1f}, estimated {80 * fitted.mean():5.1f}, "
              f"r={np.corrcoef(truth, fitted)[0, 1]:.2f}, mean absolute error {80 * np.abs(truth - fitted).mean():.1f}")


if __name__ == "__main__":
    main()
