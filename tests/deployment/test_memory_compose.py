import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class MemoryBudgetTests(unittest.TestCase):
    def services(self):
        environment = {
            "PATH": os.environ["PATH"],
            "HOME": os.environ.get("HOME", ""),
            "POSTGRES_PASSWORD": "postgres",
            "LOKI_URL": "https://logs.example.invalid",
            "LOKI_USERNAME": "fixture",
            "GRAFANA_CLOUD_API_KEY": "fixture",
        }
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
            return json.loads(result.stdout)["services"]

    def test_backend_container_has_a_memory_limit(self):
        self.assertEqual(self.services()["backend"].get("mem_limit"), "1073741824")

    def test_backend_limits_glibc_malloc_arenas(self):
        self.assertEqual(self.services()["backend"]["environment"].get("MALLOC_ARENA_MAX"), "2")

    def test_database_memory_settings_fit_the_shared_vps(self):
        command = self.services()["timescaledb"]["command"]
        settings = dict(command[i + 1].split("=", 1) for i, flag in enumerate(command) if flag == "-c")
        self.assertEqual(settings["shared_buffers"], "256MB")
        self.assertEqual(settings["maintenance_work_mem"], "64MB")
        self.assertEqual(settings["max_connections"], "50")


if __name__ == "__main__":
    unittest.main()
