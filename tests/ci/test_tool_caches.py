import hashlib
import io
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import tomllib
import unittest
import zipfile

import yaml

ROOT = Path(__file__).resolve().parents[2]
SHA256 = re.compile(r'\b[0-9a-f]{64}\b')
INSTALL_BUN = 'sh scripts/ci/install-bun.sh'
RUNS_BUN = re.compile(r'(?<![\w./-])bunx?(?![\w.-])')
INSTALL_TRIVY = 'sh scripts/ci/install-trivy.sh'
FAKE_CURL = r'''#!/usr/bin/env python3
import os, shutil, sys
args = sys.argv[1:]
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(" ".join(args) + "\n")
shutil.copy(os.environ["FIXTURE_DOWNLOAD"], args[args.index("--output") + 1])
'''


def steps_using(action, workflows=ROOT / '.github/workflows'):
    for workflow in sorted(workflows.glob('*.y*ml')):
        for job_name, job in yaml.safe_load(workflow.read_text())['jobs'].items():
            steps = job.get('steps', [])
            for index, step in enumerate(steps):
                if step.get('uses', '').lower().startswith(action + '@'):
                    yield f'{workflow.name}:{job_name}[{index}]', steps, index, step.get('with', {})


def bun_archive(script):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as archive:
        for name, body in [('bun-linux-x64/bun', script), ('bun-linux-x64/README.md', b'readme')]:
            entry = zipfile.ZipInfo(name)
            entry.create_system = 3
            entry.external_attr = 0o100755 << 16
            archive.writestr(entry, body)
    return buffer.getvalue()


def trivy_archive(script):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode='w:gz') as archive:
        for name, body in [('trivy', script), ('README.md', b'readme')]:
            entry = tarfile.TarInfo(name)
            entry.size = len(body)
            entry.mode = 0o755
            archive.addfile(entry, io.BytesIO(body))
    return buffer.getvalue()


class ToolCacheTests(unittest.TestCase):
    maxDiff = None

    def test_no_workflow_installs_bun_with_setup_bun(self):
        self.assertEqual([source for source, _, _, _ in steps_using('oven-sh/setup-bun')], [])

    def test_every_trivy_scan_skips_the_binary_and_database_caches(self):
        uses = list(steps_using('aquasecurity/trivy-action'))
        self.assertGreaterEqual(len(uses), 1)
        problems = {}
        for source, _, _, inputs in uses:
            if inputs.get('cache') is not False:
                problems[f'{source} cache'] = inputs.get('cache')
            if inputs.get('skip-setup-trivy') is not True:
                problems[f'{source} skip-setup-trivy'] = inputs.get('skip-setup-trivy')
        self.assertEqual(problems, {})

    def test_matches_the_actions_in_any_letter_case_and_any_workflow_file(self):
        with tempfile.TemporaryDirectory() as directory:
            workflows = Path(directory)
            (workflows / 'other.yaml').write_text('jobs:\n  build:\n    steps:\n      - uses: Oven-Sh/Setup-Bun@' + 'a' * 40 + '\n      - uses: AquaSecurity/Trivy-Action@' + 'b' * 40 + '\n')
            self.assertEqual([source for source, _, _, _ in steps_using('oven-sh/setup-bun', workflows)], ['other.yaml:build[0]'])
            self.assertEqual([source for source, _, _, _ in steps_using('aquasecurity/trivy-action', workflows)], ['other.yaml:build[1]'])


class InstallScriptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        (self.bin / 'curl').write_text(FAKE_CURL)
        (self.bin / 'curl').chmod(0o755)

    def run_install(self, script, download):
        runner_temp = self.root / 'runner-temp'
        runner_temp.mkdir(exist_ok=True)
        github_path = self.root / 'github-path'
        github_path.write_text('')
        fixture = self.root / 'download'
        fixture.write_bytes(download)
        step = self.root / 'step.sh'
        step.write_text(script)
        result = subprocess.run(['sh', str(step)], cwd=self.root, capture_output=True, text=True, timeout=30,
                                env={'PATH': str(self.bin) + os.pathsep + os.environ['PATH'], 'RUNNER_TEMP': str(runner_temp),
                                     'GITHUB_PATH': str(github_path), 'FIXTURE_DOWNLOAD': str(fixture), 'FIXTURE_LOG': str(self.root / 'curl.log')})
        added = [Path(line) for line in github_path.read_text().splitlines()]
        return result, added


class PinnedTrivyInstallTests(InstallScriptTests):
    def setUp(self):
        uses = list(steps_using('aquasecurity/trivy-action'))
        self.assertGreaterEqual(len(uses), 1)
        super().setUp()
        self.scans = uses

    def install_script(self, source, steps, index):
        self.assertEqual([step.get('run') for step in steps[:index] if 'trivy' in step.get('run', '')], [INSTALL_TRIVY], source)
        return (ROOT / INSTALL_TRIVY.split()[1]).read_text()

    def test_each_scan_runs_after_one_step_that_installs_a_sha256_pinned_trivy(self):
        for source, steps, index, _ in self.scans:
            with self.subTest(source=source):
                self.assertEqual(len(SHA256.findall(self.install_script(source, steps, index))), 1)

    def test_a_download_that_does_not_match_the_pin_fails_before_trivy_reaches_the_path(self):
        for source, steps, index, _ in self.scans:
            with self.subTest(source=source):
                script = self.install_script(source, steps, index)
                result, added = self.run_install(script, trivy_archive(b'#!/bin/sh\necho tampered\n'))
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('FAILED', result.stdout + result.stderr)
                self.assertEqual(added, [])
                self.assertEqual(list((self.root / 'runner-temp').rglob('trivy')), [])
                [request] = [line.split() for line in (self.root / 'curl.log').read_text().splitlines()]
                self.assertIn('--proto =https', ' '.join(request))
                self.assertEqual([arg for arg in request if '://' in arg and not arg.startswith('https://')], [])

    def test_a_download_that_matches_the_pin_puts_only_that_trivy_on_the_path(self):
        archive = trivy_archive(b'#!/bin/sh\necho genuine\n')
        for source, steps, index, _ in self.scans:
            with self.subTest(source=source):
                script = SHA256.sub(hashlib.sha256(archive).hexdigest(), self.install_script(source, steps, index))
                result, added = self.run_install(script, archive)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(len(added), 1)
                self.assertEqual(sorted(path.name for path in added[0].iterdir()), ['trivy'])
                self.assertEqual(subprocess.run([added[0] / 'trivy'], capture_output=True, text=True).stdout, 'genuine\n')


class PinnedBunInstallTests(InstallScriptTests):
    def setUp(self):
        super().setUp()
        self.workflow = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())
        self.script = ROOT / INSTALL_BUN.split()[1]

    def test_mise_does_not_install_bun_in_ci(self):
        self.assertIn('bun', self.workflow['env']['MISE_DISABLE_TOOLS'].split(','))

    def test_each_job_that_runs_bun_installs_the_pinned_bun_before_it(self):
        users, problems = [], {}
        for name, job in self.workflow['jobs'].items():
            runs = [step.get('run', '') for step in job.get('steps', [])]
            first_bun = next((i for i, run in enumerate(runs) if RUNS_BUN.search(run)), None)
            if first_bun is None:
                continue
            users.append(name)
            installs = [i for i, run in enumerate(runs) if run.strip() == INSTALL_BUN]
            if len(installs) != 1 or installs[0] > first_bun:
                problems[name] = {'installs': installs, 'first bun': first_bun}
        self.assertIn('release', users)
        self.assertEqual(problems, {})

    def test_the_pinned_bun_is_the_mise_toml_version(self):
        version = re.search(r'^version=(\S+)$', self.script.read_text(), re.MULTILINE)
        self.assertIsNotNone(version)
        self.assertEqual(version.group(1), tomllib.loads((ROOT / '.mise.toml').read_text())['tools']['bun'])

    def test_a_download_that_does_not_match_the_pin_fails_before_bun_reaches_the_path(self):
        result, added = self.run_install(self.script.read_text(), bun_archive(b'#!/bin/sh\necho tampered\n'))
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('FAILED', result.stdout + result.stderr)
        self.assertEqual(added, [])
        self.assertEqual([path for path in (self.root / 'runner-temp').rglob('*') if path.name in {'bun', 'bunx'}], [])
        [request] = [line.split() for line in (self.root / 'curl.log').read_text().splitlines()]
        self.assertIn('--proto =https', ' '.join(request))
        self.assertEqual([arg for arg in request if '://' in arg and not arg.startswith('https://')], [])

    def test_a_download_that_matches_the_pin_puts_only_bun_and_bunx_on_the_path(self):
        archive = bun_archive(b'#!/bin/sh\necho genuine "${0##*/}"\n')
        script = self.script.read_text()
        self.assertEqual(len(SHA256.findall(script)), 1)
        result, added = self.run_install(SHA256.sub(hashlib.sha256(archive).hexdigest(), script), archive)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(added), 1)
        self.assertEqual(sorted(path.name for path in added[0].iterdir()), ['bun', 'bunx'])
        self.assertEqual((added[0] / 'bunx').resolve(), (added[0] / 'bun').resolve())
        for name in ['bun', 'bunx']:
            self.assertEqual(subprocess.run([added[0] / name], capture_output=True, text=True).stdout, f'genuine {name}\n')



if __name__ == '__main__':
    unittest.main()
