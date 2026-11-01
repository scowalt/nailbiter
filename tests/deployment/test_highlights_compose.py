import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class HighlightsComposeTests(unittest.TestCase):
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
            return json.loads(result.stdout)

    def test_discovery_and_catchup_remain_disabled_without_doppler_settings(self):
        environment = self.compose()["services"]["backend"]["environment"]
        self.assertEqual(
            {name: value for name, value in environment.items() if name.startswith("Highlights__")},
            {
                "Highlights__YouTube__Enabled": "false",
                "Highlights__YouTube__ApiKey": "",
                "Highlights__YouTube__DailyUnitLimit": "1000",
                "Highlights__YouTube__MaxUploadPages": "2",
                "Highlights__Worker__Enabled": "false",
                "Highlights__Worker__MaxGamesPerCycle": "100",
                "Highlights__Worker__MaxCatchupPages": "16",
                "Highlights__Worker__AuthorizeLaunchCatchup": "false",
            },
        )

    def test_authorized_doppler_settings_allow_bounded_discovery_without_catchup(self):
        environment = self.compose({
            "HIGHLIGHTS_YOUTUBE_ENABLED": "true",
            "HIGHLIGHTS_YOUTUBE_API_KEY": "fake_youtube_key_for_compose_tests",
            "HIGHLIGHTS_YOUTUBE_DAILY_UNIT_LIMIT": "650",
            "HIGHLIGHTS_YOUTUBE_MAX_UPLOAD_PAGES": "1",
            "HIGHLIGHTS_WORKER_ENABLED": "true",
            "HIGHLIGHTS_WORKER_MAX_GAMES_PER_CYCLE": "25",
            "HIGHLIGHTS_WORKER_MAX_CATCHUP_PAGES": "4",
        })["services"]["backend"]["environment"]
        self.assertEqual(
            {name: value for name, value in environment.items() if name.startswith("Highlights__")},
            {
                "Highlights__YouTube__Enabled": "true",
                "Highlights__YouTube__ApiKey": "fake_youtube_key_for_compose_tests",
                "Highlights__YouTube__DailyUnitLimit": "650",
                "Highlights__YouTube__MaxUploadPages": "1",
                "Highlights__Worker__Enabled": "true",
                "Highlights__Worker__MaxGamesPerCycle": "25",
                "Highlights__Worker__MaxCatchupPages": "4",
                "Highlights__Worker__AuthorizeLaunchCatchup": "false",
            },
        )

    def test_youtube_key_is_only_forwarded_to_the_backend(self):
        services = self.compose({
            "HIGHLIGHTS_YOUTUBE_API_KEY": "fake_youtube_key_for_compose_tests",
        })["services"]
        self.assertEqual(
            {name for name, service in services.items()
             if "fake_youtube_key_for_compose_tests" in service.get("environment", {}).values()},
            {"backend"},
        )

    def test_worker_can_stop_independently_for_rollback(self):
        environment = self.compose({
            "HIGHLIGHTS_YOUTUBE_ENABLED": "true",
            "HIGHLIGHTS_YOUTUBE_API_KEY": "fake_youtube_key_for_compose_tests",
            "HIGHLIGHTS_WORKER_ENABLED": "false",
            "HIGHLIGHTS_YOUTUBE_DAILY_UNIT_LIMIT": "200",
        })["services"]["backend"]["environment"]
        self.assertEqual(
            (environment["Highlights__YouTube__Enabled"],
             environment["Highlights__Worker__Enabled"],
             environment["Highlights__Worker__AuthorizeLaunchCatchup"]),
            ("true", "false", "false"),
        )


if __name__ == "__main__":
    unittest.main()
