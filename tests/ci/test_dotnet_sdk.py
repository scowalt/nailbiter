import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import tomllib
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
INSTALL = 'sh scripts/ci/install-dotnet-sdk.sh'
CHECK = 'sh scripts/ci/check-dotnet-sdk.sh'
SDK = json.loads((ROOT / 'apps/backend/global.json').read_text())['sdk']['version']
ARCHIVE = f'dotnet-sdk-{SDK}-linux-x64.tar.gz'
URL = f'https://builds.dotnet.microsoft.com/dotnet/Sdk/{SDK}/{ARCHIVE}'
FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, shutil, sys
args = sys.argv[1:]
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")
if os.environ.get("FIXTURE_CURL_EXIT", "0") != "0":
    sys.exit(int(os.environ["FIXTURE_CURL_EXIT"]))
shutil.copyfile(os.environ["FIXTURE_TARBALL"], args[args.index("--output") + 1])
'''
FAKE_DOTNET = '#!/bin/sh\ntouch "$FIXTURE_EXECUTED"\n'


def mise_dotnet_tool():
    tools = tomllib.loads((ROOT / '.mise.toml').read_text())['tools']
    return next(name for name in tools if 'dotnet' in name)


class DotnetSdkWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.workflow = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())
        self.jobs = self.workflow['jobs']

    def test_every_job_that_runs_dotnet_installs_the_pinned_sdk_first(self):
        users = []
        runs_dotnet = r'(?<![\w-])dotnet(?![\w-])|package-listing-check\.sh|backend-test-shard\.py|scripts/test-backend'
        for name, job in self.jobs.items():
            runs = [step.get('run', '') for step in job['steps']]
            first_dotnet = next((i for i, run in enumerate(runs) if run.strip() not in (INSTALL, CHECK) and re.search(runs_dotnet, run)), None)
            if first_dotnet is None:
                continue
            users.append(name)
            installs = [i for i, run in enumerate(runs) if run.strip() == INSTALL]
            self.assertEqual(len(installs), 1, name)
            self.assertEqual([i for i, run in enumerate(runs) if run.strip() == CHECK], [installs[0] + 1], name)
            self.assertLess(installs[0] + 1, first_dotnet, name)
        self.assertEqual(sorted(users), ['backend-tests', 'checks', 'listing-checker'])

    def test_no_job_installs_dotnet_through_the_mise_plugin(self):
        self.assertEqual(list(self.workflow['env']), ['MISE_DISABLE_TOOLS'])
        self.assertIn(mise_dotnet_tool(), self.workflow['env']['MISE_DISABLE_TOOLS'].split(','))
        for name, job in self.jobs.items():
            self.assertNotIn('MISE_DISABLE_TOOLS', job.get('env', {}), name)
            for step in job['steps']:
                self.assertNotIn('dotnet', json.dumps(step.get('with', {})), name)
                self.assertNotIn('dotnet-install', step.get('run', ''), name)
                self.assertNotIn('GITHUB_PATH', step.get('run', ''), name)

    def test_the_committed_sha512_names_exactly_the_global_json_sdk(self):
        lines = (ROOT / 'scripts/ci/dotnet-sdk.sha512').read_text().splitlines()
        self.assertEqual(len(lines), 1)
        self.assertRegex(lines[0], rf'^[0-9a-f]{{128}}  {re.escape(ARCHIVE)}$')
        self.assertEqual(tomllib.loads((ROOT / '.mise.toml').read_text())['tools'][mise_dotnet_tool()], SDK)


class DotnetSdkInstallTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.workspace = self.root / 'workspace'
        (self.workspace / 'apps/backend').mkdir(parents=True)
        (self.workspace / 'scripts/ci').mkdir(parents=True)
        (self.workspace / 'apps/backend/global.json').write_bytes((ROOT / 'apps/backend/global.json').read_bytes())
        self.runner_temp = self.root / 'runner-temp'
        self.runner_temp.mkdir()
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        (self.bin / 'curl').write_text(FAKE_CURL)
        (self.bin / 'curl').chmod(0o755)
        self.log = self.root / 'curl.jsonl'
        self.executed = self.root / 'executed'
        self.github_path = self.root / 'github-path'
        self.tarball = self.root / 'fixture.tar.gz'
        self.tarball.write_bytes(self.sdk_tarball())
        self.env = {'PATH': str(self.bin) + os.pathsep + os.environ['PATH'], 'HOME': str(self.root), 'RUNNER_TEMP': str(self.runner_temp),
                    'GITHUB_PATH': str(self.github_path), 'FIXTURE_LOG': str(self.log), 'FIXTURE_TARBALL': str(self.tarball),
                    'FIXTURE_EXECUTED': str(self.executed)}

    @staticmethod
    def sdk_tarball():
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode='w:gz') as archive:
            data = FAKE_DOTNET.encode()
            entry = tarfile.TarInfo('./dotnet')
            entry.size = len(data)
            entry.mode = 0o755
            archive.addfile(entry, io.BytesIO(data))
        return buffer.getvalue()

    def pin(self, data, name=ARCHIVE):
        (self.workspace / 'scripts/ci/dotnet-sdk.sha512').write_text(f'{hashlib.sha512(data).hexdigest()}  {name}\n')

    def install(self, env=None):
        return subprocess.run(['sh', str(ROOT / 'scripts/ci/install-dotnet-sdk.sh')], cwd=self.workspace, env=self.env | (env or {}),
                              capture_output=True, text=True, timeout=30)

    def downloads(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def assert_nothing_installed(self, result):
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse((self.runner_temp / 'dotnet').exists())
        self.assertFalse(self.github_path.exists())
        self.assertFalse(self.executed.exists())

    def test_matching_tarball_is_extracted_onto_the_path_without_running_it(self):
        self.pin(self.tarball.read_bytes())
        result = self.install()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.github_path.read_text(), f'{self.runner_temp}/dotnet\n')
        self.assertEqual((self.runner_temp / 'dotnet/dotnet').read_text(), FAKE_DOTNET)
        self.assertFalse(self.executed.exists())
        self.assertEqual([path.name for path in self.runner_temp.iterdir()], ['dotnet'])
        [download] = self.downloads()
        self.assertEqual(download[-1], URL)
        self.assertEqual(download[download.index('--proto') + 1], '=https')
        self.assertEqual(download[download.index('--proto-redir') + 1], '=https')
        self.assertIn('--tlsv1.2', download)

    def test_tarball_that_does_not_match_the_committed_sha512_fails_before_extraction(self):
        (self.workspace / 'scripts/ci/dotnet-sdk.sha512').write_bytes((ROOT / 'scripts/ci/dotnet-sdk.sha512').read_bytes())
        result = self.install()
        self.assert_nothing_installed(result)
        self.assertIn(f'::error title=.NET SDK checksum mismatch::{ARCHIVE}', result.stdout)

    def test_one_changed_byte_fails_before_extraction(self):
        genuine = self.tarball.read_bytes()
        self.pin(genuine)
        self.tarball.write_bytes(genuine[:-1] + bytes([genuine[-1] ^ 1]))
        result = self.install()
        self.assert_nothing_installed(result)
        self.assertIn(f'::error title=.NET SDK checksum mismatch::{ARCHIVE}', result.stdout)

    def test_sdk_without_a_committed_sha512_is_never_downloaded(self):
        self.pin(self.tarball.read_bytes(), name=f'{ARCHIVE}.old')
        result = self.install()
        self.assert_nothing_installed(result)
        self.assertEqual(self.downloads(), [])
        self.assertIn('::error title=.NET SDK not pinned::', result.stdout)

    def test_malformed_sha512_is_never_trusted(self):
        (self.workspace / 'scripts/ci/dotnet-sdk.sha512').write_text(f'{"0" * 127}  {ARCHIVE}\n')
        result = self.install()
        self.assert_nothing_installed(result)
        self.assertEqual(self.downloads(), [])
        self.assertIn('::error title=.NET SDK not pinned::', result.stdout)

    def test_global_json_version_that_is_not_a_plain_version_is_never_downloaded(self):
        for version in ['', '10.0.401/../../evil', '10.0.401 ', 'latest']:
            with self.subTest(version=version):
                (self.workspace / 'apps/backend/global.json').write_text(json.dumps({'sdk': {'version': version}}, indent=2))
                self.pin(self.tarball.read_bytes(), name=f'dotnet-sdk-{version}-linux-x64.tar.gz')
                result = self.install()
                self.assert_nothing_installed(result)
                self.assertEqual(self.downloads(), [])
                self.assertIn('::error title=.NET SDK version unreadable::', result.stdout)

    def test_failed_download_installs_nothing(self):
        self.pin(self.tarball.read_bytes())
        self.assert_nothing_installed(self.install({'FIXTURE_CURL_EXIT': '22'}))
        self.assertEqual([download[-1] for download in self.downloads()], [URL])


class DotnetSdkCheckTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.workspace = self.root / 'workspace'
        (self.workspace / 'apps/backend').mkdir(parents=True)
        (self.workspace / 'apps/backend/global.json').write_bytes((ROOT / 'apps/backend/global.json').read_bytes())
        self.runner_temp = self.root / 'runner temp $x'
        self.pinned = self.runner_temp / 'dotnet'
        self.elsewhere = self.root / 'usr-bin'
        self.tools = self.root / 'tools'
        self.tools.mkdir()
        for tool in ['sed', 'cat', 'printf']:
            found = subprocess.run(['sh', '-c', f'command -v {tool}'], capture_output=True, text=True).stdout.strip()
            if found.startswith('/'):
                (self.tools / tool).symlink_to(found)

    def dotnet(self, directory, version):
        directory.mkdir(parents=True, exist_ok=True)
        (directory / 'dotnet').write_text(f'#!/bin/sh\n[ "$1" = --version ] && echo {version}\n')
        (directory / 'dotnet').chmod(0o755)

    def check(self, *path):
        env = {'PATH': os.pathsep.join([*map(str, path), str(self.tools)]), 'RUNNER_TEMP': str(self.runner_temp)}
        return subprocess.run([subprocess.run(['sh', '-c', 'command -v sh'], capture_output=True, text=True).stdout.strip(),
                               str(ROOT / 'scripts/ci/check-dotnet-sdk.sh')], cwd=self.workspace, env=env, capture_output=True, text=True, timeout=30)

    def test_pinned_sdk_first_on_the_path_passes_and_logs_its_version_and_location(self):
        self.dotnet(self.pinned, SDK)
        self.dotnet(self.elsewhere, SDK)
        result = self.check(self.pinned, self.elsewhere)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(f'dotnet --version: {SDK}', result.stdout)
        self.assertIn(f'command -v dotnet: {self.pinned}/dotnet', result.stdout)

    def test_another_dotnet_ahead_of_the_pinned_sdk_fails(self):
        self.dotnet(self.pinned, SDK)
        self.dotnet(self.elsewhere, SDK)
        result = self.check(self.elsewhere, self.pinned)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn('::error title=.NET SDK not on PATH::', result.stdout)

    def test_pinned_sdk_reporting_another_version_fails(self):
        self.dotnet(self.pinned, '10.0.404')
        result = self.check(self.pinned)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn('::error title=.NET SDK version mismatch::', result.stdout)

    def test_missing_dotnet_fails(self):
        result = self.check()
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn('::error title=.NET SDK not on PATH::', result.stdout)


if __name__ == '__main__':
    unittest.main()
