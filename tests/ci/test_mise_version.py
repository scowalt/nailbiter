from pathlib import Path
import re
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
VERSION = re.compile(r'^[0-9]{4}\.[0-9]{1,2}\.[0-9]+$')
SHA256 = re.compile(r'^[0-9a-f]{64}$')
X64_LINUX_RUNNER = re.compile(r'^ubuntu-[0-9]+\.[0-9]+$')


def mise_action_uses(workflows=ROOT / '.github/workflows'):
    for workflow in sorted(workflows.glob('*.y*ml')):
        for job_name, job in yaml.safe_load(workflow.read_text())['jobs'].items():
            for index, step in enumerate(job.get('steps', [])):
                if step.get('uses', '').startswith('jdx/mise-action@'):
                    yield f'{workflow.name}:{job_name}[{index}]', job, step.get('with', {})


class MiseVersionTests(unittest.TestCase):
    maxDiff = None

    def setUp(self):
        self.uses = list(mise_action_uses())

    def test_every_mise_action_use_pins_the_version_and_binary_sha256(self):
        self.assertGreaterEqual(len(self.uses), 2)
        problems = []
        for source, _, inputs in self.uses:
            if not VERSION.match(str(inputs.get('version', ''))):
                problems.append(f'{source}: version must be an exact mise release, got {inputs.get("version")!r}')
            if not SHA256.match(str(inputs.get('sha256', ''))):
                problems.append(f'{source}: sha256 must be the lowercase hex hash of the mise binary, got {inputs.get("sha256")!r}')
        self.assertEqual(problems, [])

    def test_every_mise_action_use_pins_the_same_release(self):
        pins = {(str(inputs.get('version')), str(inputs.get('sha256'))) for _, _, inputs in self.uses}
        self.assertEqual(len(pins), 1, pins)

    def test_the_pinned_binary_hash_matches_the_runner_platform(self):
        runners = {source: job.get('runs-on') for source, job, _ in self.uses}
        self.assertEqual({source: runner for source, runner in runners.items() if not X64_LINUX_RUNNER.match(str(runner))}, {})


class MiseActionMatchTests(unittest.TestCase):
    def test_matches_the_action_in_any_letter_case_and_any_workflow_file(self):
        with tempfile.TemporaryDirectory() as directory:
            workflows = Path(directory)
            (workflows / 'other.yaml').write_text('jobs:\n  build:\n    runs-on: ubuntu-24.04\n    steps:\n      - uses: JDX/Mise-Action@' + 'a' * 40 + '\n')
            self.assertEqual([source for source, _, _ in mise_action_uses(workflows)], ['other.yaml:build[0]'])


if __name__ == '__main__':
    unittest.main()
