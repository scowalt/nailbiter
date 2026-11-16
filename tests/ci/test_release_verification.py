import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
SHA = 'a' * 40
DIGEST = 'ghcr.io/scowalt/nailbiter/backend@sha256:' + 'b' * 64
FAKE_DOCKER = r'''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")
if args[:2] == ["image", "inspect"]:
    print(os.environ["FIXTURE_IMAGE_ENV"].replace(";", "\n"))
'''
FAKE_DOTNET = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")
output = pathlib.Path(args[args.index("--output") + 1])
output.mkdir(parents=True)
(output / "listing-check").write_text("checker")
(output / "listing-check").chmod(0o755)
'''
FAKE_CHECKER = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = [sys.argv[0]] + sys.argv[1:]
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")
print(os.environ["FIXTURE_OUTPUT"])
if "--report" in args:
    pathlib.Path(args[args.index("--report") + 1]).write_text('{"schema": "nailbiter.listing-check/1"}')
sys.exit(int(os.environ["FIXTURE_EXIT"]))
'''
HINTS = {
    1: 'stack trace',
    2: 'scripts/ci/verify-listings.sh',
    3: 'error status',
    4: 'complete response within 10 s',
    5: 'without its listing markup',
    7: 'over budget',
    8: 'No listing was requested',
    9: 'different revision',
}


class ReleaseVerificationScriptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'commands.jsonl'
        self.summary = self.root / 'summary.md'
        (self.root / '.ci/artifacts/listing-checker').mkdir(parents=True)
        (self.root / '.ci/backend-image.txt').write_text(DIGEST + '\n')
        (self.root / 'apps/backend').mkdir(parents=True)
        (self.root / 'apps/backend/global.json').write_bytes((ROOT / 'apps/backend/global.json').read_bytes())
        self.env = {'PATH': str(self.bin) + os.pathsep + os.environ['PATH'], 'HOME': str(self.root), 'FIXTURE_LOG': str(self.log),
                    'GITHUB_SHA': SHA, 'GITHUB_STEP_SUMMARY': str(self.summary), 'GITHUB_OUTPUT': str(self.root / 'output.txt')}
        self.executable('docker', FAKE_DOCKER)
        self.executable('dotnet', FAKE_DOTNET)
        self.package_checker()
        self.executable('gh', '#!/bin/sh\necho "the check must not depend on the current main ref" >&2\nexit 1\n')

    def package_checker(self, commit=SHA):
        staging = self.root / 'staging/listing-checker'
        staging.mkdir(parents=True)
        (staging / 'listing-check').write_text(FAKE_CHECKER)
        (staging / 'listing-check').chmod(0o755)
        subprocess.run(['tar', '-C', str(staging.parent), '-cf', '.ci/artifacts/listing-checker/listing-checker.tar', 'listing-checker'], cwd=self.root, check=True)
        self.env['LISTING_CHECKER_SHA256'] = self.checker_digest()
        (self.root / '.ci/artifacts/listing-checker/listing-checker.commit').write_text(commit + '\n')

    def checker_digest(self, archive='.ci/artifacts/listing-checker/listing-checker.tar'):
        digest = subprocess.run(['sha256sum', archive], cwd=self.root, check=True, capture_output=True, text=True)
        return digest.stdout.split()[0]

    def executable(self, name, text):
        file = self.bin / name
        file.write_text(text)
        file.chmod(0o755)

    def run_script(self, name, env=None):
        return subprocess.run(['bash', str(ROOT / 'scripts/ci' / name)], cwd=self.root, env=self.env | (env or {}),
                              capture_output=True, text=True, timeout=15)

    def commands(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def verify(self, exit_code, output='outcome: pass (exit 0) in 900 ms, load 0.1 -> 0.2', env=None):
        return self.run_script('verify-listings.sh', env={'FIXTURE_EXIT': str(exit_code), 'FIXTURE_OUTPUT': output} | (env or {}))

    def test_image_build_bakes_in_the_commit_and_proves_it_before_saving(self):
        result = self.run_script('build-image.sh', env={'FIXTURE_IMAGE_ENV': f'PATH=/usr/bin;SourceRevision={SHA};ASPNETCORE_URLS=http://+:5093'})

        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        build = next(command for command in commands if command[0] == 'build')
        self.assertIn(f'SOURCE_REVISION={SHA}', [build[i + 1] for i, arg in enumerate(build) if arg == '--build-arg'])
        self.assertEqual(commands[-1][0], 'save')

    def test_image_without_the_commit_is_never_saved(self):
        for image_env in ['PATH=/usr/bin;SourceRevision=', 'PATH=/usr/bin', f'SourceRevision={"c" * 40}', f'SourceRevision={SHA}x']:
            with self.subTest(image_env=image_env):
                self.log.unlink(missing_ok=True)
                result = self.run_script('build-image.sh', env={'FIXTURE_IMAGE_ENV': image_env})
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn('save', [command[0] for command in self.commands()])

    def test_checker_is_published_self_contained_and_bound_to_the_commit(self):
        result = self.run_script('package-listing-check.sh')

        self.assertEqual(result.returncode, 0, result.stderr)
        (publish,) = self.commands()
        self.assertEqual(publish[:2], ['publish', 'apps/listing-check/listing-check.csproj'])
        for option, value in [('--configuration', 'Release'), ('--runtime', 'linux-x64'), ('--output', '.ci/listing-checker')]:
            self.assertEqual(self.values(publish, option), [value])
        self.assertIn('--self-contained', publish)
        self.assertIn('-p:PublishSingleFile=true', publish)
        self.assertEqual((self.root / '.ci/listing-checker.commit').read_text(), SHA + '\n')
        self.assertEqual((self.root / 'output.txt').read_text(), f"sha256={self.checker_digest('.ci/listing-checker.tar')}\n")
        self.assertEqual(sorted(path.name for path in (self.root / '.ci').glob('listing-checker.*')),
                         ['listing-checker.commit', 'listing-checker.tar'])
        listing = subprocess.run(['tar', '-tf', '.ci/listing-checker.tar'], cwd=self.root, check=True, capture_output=True, text=True)
        self.assertIn('listing-checker/listing-check', listing.stdout.split())

    def test_passing_check_reports_the_verified_revision_and_image(self):
        result = self.verify(0)

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        (check,) = self.commands()
        self.assertEqual(check[:2], ['.ci/listing-checker/listing-check', 'url'])
        options = check[2:]
        self.assertEqual(self.values(options, '--base-url'), ['https://live.nailbiter.app'])
        self.assertEqual(self.values(options, '--expected-revision'), [SHA])
        self.assertEqual(self.values(options, '--ready-within-ms'), ['120000'])
        self.assertEqual(self.values(options, '--path'), ['/', '/nfl', '/nba', '/nhl', '/mlb', '/cfb'])
        self.assertEqual(self.values(options, '--samples'), ['3'])
        self.assertEqual(self.values(options, '--warmup'), ['1'])
        self.assertEqual(self.values(options, '--ttfb-budget-ms'), ['1500'])
        self.assertEqual(self.values(options, '--completion-budget-ms'), ['2500'])
        self.assertEqual(self.values(options, '--request-timeout-ms'), ['10000'])
        self.assertEqual(self.values(options, '--report'), ['.ci/listing-check.json'])
        self.assertIn('--allow-remote-host', options)
        self.assertNotIn('::error', result.stdout)
        self.assertIn('outcome: pass (exit 0)', result.stdout)
        summary = self.summary.read_text()
        for expected in [SHA, DIGEST, 'https://live.nailbiter.app', 'outcome: pass (exit 0)', f'listing-check-{SHA}', 'Verified']:
            self.assertIn(expected, summary)
        self.assertTrue((self.root / '.ci/listing-check.json').exists())
        self.assertEqual((self.root / '.ci/listing-check.txt').read_text().strip(), 'outcome: pass (exit 0) in 900 ms, load 0.1 -> 0.2')

    def test_each_failure_class_fails_the_step_with_an_actionable_annotation(self):
        for exit_code, hint in HINTS.items():
            with self.subTest(exit_code=exit_code):
                self.summary.unlink(missing_ok=True)
                result = self.verify(exit_code, output=f'outcome: fixture (exit {exit_code})')
                self.assertEqual(result.returncode, exit_code)
                annotation = next(line for line in result.stdout.splitlines() if line.startswith('::error '))
                self.assertTrue(annotation.startswith(f'::error title=Deployed listing check failed (exit {exit_code})::'), annotation)
                self.assertIn(hint, annotation)
                summary = self.summary.read_text()
                self.assertIn(hint, summary)
                self.assertIn(f'outcome: fixture (exit {exit_code})', summary)
                self.assertNotIn('Verified', summary)

    def test_a_different_revision_is_named_with_both_revisions_without_guessing_its_age(self):
        other = 'c' * 40
        lines = {
            8: f'readiness /health: revision {SHA} not served within 120000 ms after 25 polls; last poll: served revision {other}',
            9: f'revision /nba: measured round 0: served revision {other}, expected {SHA}',
        }
        for exit_code, line in lines.items():
            with self.subTest(exit_code=exit_code):
                self.summary.unlink(missing_ok=True)
                result = self.verify(exit_code, output=f'{line}\noutcome: fixture (exit {exit_code})')
                annotation = next(line for line in result.stdout.splitlines() if line.startswith('::error '))
                self.assertIn('different revision', annotation)
                self.assertIn(f'expected {SHA}', annotation)
                self.assertIn(f'served revision {other}', annotation)
                for guess in ['previous', 'older', 'old container', 'second', 'stale']:
                    self.assertNotIn(guess, annotation.lower())
                    self.assertNotIn(guess, self.summary.read_text().lower())

    def test_a_checker_without_its_commit_and_job_reported_hash_sends_no_request(self):
        for tamper in ['commit', 'bytes', 'bytes with a matching hash file', 'missing', 'no job hash', 'malformed job hash']:
            with self.subTest(tamper=tamper):
                self.log.unlink(missing_ok=True)
                self.summary.unlink(missing_ok=True)
                shutil.rmtree(self.root / 'staging', ignore_errors=True)
                self.package_checker(commit='c' * 40 if tamper == 'commit' else SHA)
                env = {}
                if tamper.startswith('bytes'):
                    with open(self.root / '.ci/artifacts/listing-checker/listing-checker.tar', 'ab') as archive:
                        archive.write(b'x')
                if tamper == 'bytes with a matching hash file':
                    (self.root / '.ci/artifacts/listing-checker/listing-checker.sha256').write_text(f'{self.checker_digest()}  .ci/artifacts/listing-checker/listing-checker.tar\n')
                if tamper == 'missing':
                    (self.root / '.ci/artifacts/listing-checker/listing-checker.tar').unlink()
                if tamper == 'no job hash':
                    env = {'LISTING_CHECKER_SHA256': ''}
                if tamper == 'malformed job hash':
                    env = {'LISTING_CHECKER_SHA256': self.env['LISTING_CHECKER_SHA256'].upper()}
                result = self.verify(0, env=env)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(self.commands(), [])
                self.assertFalse((self.root / '.ci/listing-checker/listing-check').exists())
                self.assertIn('::error title=Deployed listing check failed (exit 2)::The listing-checker artifact', result.stdout)
                self.assertIn('The listing-checker artifact', self.summary.read_text())

    def test_a_failure_without_a_checker_line_omits_the_checker_clause(self):
        for exit_code in [8, 9]:
            with self.subTest(exit_code=exit_code):
                result = self.verify(exit_code, output=f'outcome: fixture (exit {exit_code})')
                annotation = next(line for line in result.stdout.splitlines() if line.startswith('::error '))
                self.assertNotIn('Checker:', annotation)
                self.assertNotIn('..', annotation)
                self.assertNotIn('  ', annotation)

    def test_malformed_revision_sends_no_request(self):
        for sha in ['', 'main', 'A' * 40, SHA + ';exit 0']:
            with self.subTest(sha=sha):
                self.log.unlink(missing_ok=True)
                self.assertNotEqual(self.verify(0, env={'GITHUB_SHA': sha}).returncode, 0)
                self.assertEqual(self.commands(), [])

    @staticmethod
    def values(options, flag):
        return [options[i + 1] for i, option in enumerate(options) if option == flag]


class ReleaseVerificationWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.release = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())['jobs']['release']
        self.steps = self.release['steps']

    def step(self, predicate):
        return next(i for i, step in enumerate(self.steps) if predicate(step))

    def test_listings_are_verified_inside_the_serialized_release_after_the_backend_deploys(self):
        self.assertNotIn('mise-action', json.dumps(self.steps))
        self.assertNotIn('dotnet ', json.dumps(self.steps))
        checker = self.step(lambda step: step.get('uses', '').startswith('actions/download-artifact@')
                            and step['with']['name'] == 'listing-checker-${{ github.sha }}')
        self.assertEqual(self.steps[checker]['with']['path'], '.ci/artifacts/listing-checker')
        backend = self.step(lambda step: step.get('id') == 'backend')
        self.assertLess(checker, backend)
        self.assertEqual(self.steps[backend]['run'], 'bash scripts/ci/send-release.sh')
        marketing = self.step(lambda step: step.get('name') == 'Deploy the tested marketing artifact')
        listings = self.step(lambda step: step.get('id') == 'listings')
        upload = self.step(lambda step: step.get('uses', '').startswith('actions/upload-artifact@'))
        self.assertLess(backend, marketing)
        self.assertLess(marketing, listings)
        self.assertLess(listings, upload)
        self.assertEqual(self.steps[listings]['if'], "${{ !cancelled() && steps.backend.outcome == 'success' }}")
        self.assertEqual(self.steps[listings]['run'], 'bash scripts/ci/verify-listings.sh')
        self.assertEqual(self.steps[listings]['env'], {'LISTING_CHECKER_SHA256': '${{ needs.listing-checker.outputs.sha256 }}'})
        self.assertNotIn('secrets.', json.dumps(self.steps[listings]))
        self.assertNotIn('token', json.dumps(self.steps[listings]).lower())
        artifact = self.steps[upload]
        self.assertEqual(artifact['if'], "${{ !cancelled() && steps.listings.outcome != 'skipped' }}")
        self.assertEqual(artifact['with']['name'], 'listing-check-${{ github.sha }}')
        self.assertEqual(artifact['with']['path'].split(), ['.ci/listing-check.json', '.ci/listing-check.txt'])
        self.assertEqual(artifact['with']['retention-days'], 90)
        self.assertEqual(self.release['concurrency'], {'group': 'production-release', 'cancel-in-progress': False})

    def test_checker_is_built_in_its_own_job_that_runs_no_package_manager_or_browser_tooling(self):
        jobs = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())['jobs']
        packaging = [name for name, job in jobs.items() if 'package-listing-check.sh' in json.dumps(job)]
        self.assertEqual(packaging, ['listing-checker'])
        job = jobs['listing-checker']
        self.assertIn('listing-checker', self.release['needs'])
        for key in ['needs', 'permissions', 'environment', 'services', 'container']:
            self.assertNotIn(key, job)
        serialized = json.dumps(job)
        self.assertNotIn('secrets.', serialized)
        for tool in ['pip', 'bun', 'bunx', 'playwright', 'uv', 'npm', 'npx', 'python', 'apt-get']:
            self.assertNotRegex(json.dumps(job['steps']), rf'(?<![\w-]){re.escape(tool)}(?![\w-])')
        steps = job['steps']
        actions = [step['uses'].split('@')[0] for step in steps if 'uses' in step]
        self.assertEqual(actions, ['actions/checkout', 'actions/upload-artifact'])
        runs = [step['run'] for step in steps if 'run' in step]
        self.assertEqual(runs, ['sh scripts/ci/install-dotnet-sdk.sh', 'sh scripts/ci/check-dotnet-sdk.sh', 'sh scripts/ci/verify-checkout.sh\nsh scripts/ci/package-listing-check.sh\n'])
        package = next(i for i, step in enumerate(steps) if 'package-listing-check.sh' in step.get('run', ''))
        self.assertEqual(steps[package]['env'], {'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'})
        self.assertEqual(job['outputs'], {'sha256': f"${{{{ steps.{steps[package]['id']}.outputs.sha256 }}}}"})
        upload = next(i for i, step in enumerate(steps) if step.get('uses', '').startswith('actions/upload-artifact@')
                      and step['with']['name'] == 'listing-checker-${{ github.sha }}')
        self.assertLess(package, upload)
        artifact = steps[upload]['with']
        self.assertEqual(artifact['path'].split(), ['.ci/listing-checker.tar', '.ci/listing-checker.commit'])
        self.assertEqual(artifact['if-no-files-found'], 'error')
        self.assertTrue(artifact['include-hidden-files'])


if __name__ == '__main__':
    unittest.main()
