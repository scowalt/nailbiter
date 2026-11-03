import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class MoneyPuckComposeTests(unittest.TestCase):
    def compose(self, settings=None):
        environment = {
            "PATH": os.environ["PATH"],
            "HOME": os.environ.get("HOME", ""),
            "POSTGRES_PASSWORD": "postgres",
            "LOKI_URL": "https://logs.example.invalid",
            "LOKI_USERNAME": "fixture",
            "GRAFANA_CLOUD_API_KEY": "fixture",
        }
        environment.update(settings or {})
        with tempfile.TemporaryDirectory() as directory:
            env_file = Path(directory) / "empty.env"
            env_file.write_text("")
            result = subprocess.run(
                [
                    "docker", "compose", "--env-file", str(env_file),
                    "--file", str(ROOT / "docker-compose.yml"),
                    "config", "--format", "json",
                ],
                cwd=ROOT,
                env=environment,
                check=True,
                capture_output=True,
                text=True,
                timeout=30,
            )
            return json.loads(result.stdout)["services"]["backend"]["environment"]

    def test_normal_deployment_collects_and_selects_approved_forecasts(self):
        environment = self.compose()
        self.assertEqual(environment.get("PlayoffRatings__MoneyPuckRefreshEnabled"), "true")
        self.assertEqual(environment.get("PlayoffRatings__MoneyPuckSelectionEnabled"), "true")
        self.assertEqual(environment.get("PlayoffRatings__MoneyPuckApprovedSeasons__0"), "2027")
        self.assertEqual(environment.get("Logging__LogLevel__System.Net.Http.HttpClient.MoneyPuckService"), "Information")

    def test_operator_stops_are_independent_and_do_not_change_approved_season(self):
        for refresh, selection in [("true", "false"), ("false", "true"), ("false", "false")]:
            with self.subTest(refresh=refresh, selection=selection):
                environment = self.compose({
                    "MONEYPUCK_REFRESH_ENABLED": refresh,
                    "MONEYPUCK_SELECTION_ENABLED": selection,
                })
                self.assertEqual(environment["PlayoffRatings__MoneyPuckRefreshEnabled"], refresh)
                self.assertEqual(environment["PlayoffRatings__MoneyPuckSelectionEnabled"], selection)
                self.assertEqual(environment["PlayoffRatings__MoneyPuckApprovedSeasons__0"], "2027")


if __name__ == "__main__":
    unittest.main()
