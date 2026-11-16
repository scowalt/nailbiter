import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
IMAGE = "ghcr.io/scowalt/nailbiter/backend@sha256:" + "b" * 64


class ServedRevisionComposeTests(unittest.TestCase):
    def test_release_compose_leaves_the_revision_baked_into_the_image(self):
        environment = {
            "PATH": os.environ["PATH"],
            "HOME": os.environ.get("HOME", ""),
            "BACKEND_IMAGE": IMAGE,
            "SourceRevision": "c" * 40,
            "SOURCE_REVISION": "c" * 40,
            "GITHUB_SHA": "c" * 40,
        }
        with tempfile.TemporaryDirectory() as directory:
            env_file = Path(directory) / "empty.env"
            env_file.write_text("")
            result = subprocess.run(
                [
                    "docker", "compose", "--env-file", str(env_file),
                    "--file", str(ROOT / "docker-compose.yml"),
                    "--file", str(ROOT / "scripts/ci/compose.release.yaml"),
                    "config", "--format", "json",
                ],
                cwd=ROOT,
                env=environment,
                check=True,
                capture_output=True,
                text=True,
                timeout=30,
            )
        backend = json.loads(result.stdout)["services"]["backend"]
        self.assertEqual(backend["image"], IMAGE)
        self.assertNotIn("SourceRevision", backend.get("environment", {}))
        self.assertNotIn("env_file", backend)


if __name__ == "__main__":
    unittest.main()
