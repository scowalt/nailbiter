import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import tomllib
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
SHA = 'a' * 40
IMAGE = 'ghcr.io/scowalt/nailbiter/backend'
DIGEST = IMAGE + '@sha256:' + 'b' * 64
LEGACY_DIGEST = 'ghcr.io/scowalt/nailbiter-app/backend@sha256:' + 'b' * 64


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.workflow = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())
        self.jobs = self.workflow['jobs']

    def test_hosted_readonly_pr_validation_and_single_guarded_release(self):
        self.assertEqual(self.workflow['permissions'], {'contents': 'read'})
        events = self.workflow.get('on', self.workflow.get(True))
        self.assertEqual(set(events), {'push', 'pull_request', 'workflow_dispatch'})
        self.assertEqual(self.workflow['concurrency']['cancel-in-progress'], "${{ github.event_name == 'pull_request' }}")
        for job in self.jobs.values():
            self.assertEqual(job['runs-on'], 'ubuntu-24.04')
            self.assertNotIn('container', job)
        for name in ['checks', 'backend-tests', 'validate', 'image']:
            serialized = json.dumps(self.jobs[name])
            self.assertNotIn('secrets.', serialized)
            self.assertTrue(all(value in {'read', 'none'} for value in self.jobs[name].get('permissions', self.workflow['permissions']).values()))
            self.assertNotIn('environment', self.jobs[name])
        release = self.jobs['release']
        self.assertEqual(release['needs'], ['validate', 'image'])
        self.assertEqual(release['environment'], 'production')
        self.assertEqual(release['concurrency'], {'group': 'production-release', 'cancel-in-progress': False})
        for guard in ["github.repository == 'scowalt/nailbiter'", "github.ref == 'refs/heads/main'", "vars.PRODUCTION_RELEASES_ENABLED == 'true'", "github.event_name == 'push'", "github.event_name == 'workflow_dispatch'"]:
            self.assertIn(guard, release['if'])
        self.assertFalse((ROOT / '.woodpecker').exists())

    def test_action_pins_and_checkout_credentials(self):
        for job in self.jobs.values():
            for step in job['steps']:
                if 'uses' not in step:
                    continue
                self.assertRegex(step['uses'], r'^[\w/-]+@[0-9a-f]{40}$')
                if step['uses'].startswith('actions/checkout@'):
                    self.assertFalse(step['with']['persist-credentials'])

    def test_all_validation_contracts_remain(self):
        text = json.dumps([self.jobs[name] for name in ['checks', 'backend-tests', 'validate']])
        for expected in ['.comment-policy/check.py', 'unittest discover -s .comment-policy', 'bun run lint', 'bun run test:live-sync', 'playwright install --with-deps chromium', 'scripts/test-fixtures/generate.py --check', 'tests/deployment', 'tests/ci', 'tests/fixtures', 'RUN_RECOVERY_DB_TESTS=1', 'PYTHONPATH=src', 'build --outDir', 'dotnet build apps/backend/backend.csproj --no-restore --configuration Release', 'dotnet test apps/backend.fsharp.tests/backend.fsharp.tests.fsproj --no-restore --configuration Release', 'dotnet build apps/backend.tests/backend.tests.csproj --no-restore --configuration Release', 'scripts/ci/backend-test-shard.py ${{ strategy.job-index }} ${{ strategy.job-total }}', 'bun run --cwd apps/www build', 'bun run test:icons']:
            self.assertIn(expected, text)
        self.assertNotIn('pilot:evaluate', text)
        tools = tomllib.loads((ROOT / '.mise.toml').read_text())['tools']
        sdk = json.loads((ROOT / 'apps/backend/global.json').read_text())['sdk']['version']
        self.assertEqual(tools['asdf:mise-plugins/mise-dotnet-core'], sdk)
        self.assertTrue(tools['python'].startswith('3.14.'))
        release_bun = next(step for step in self.jobs['release']['steps'] if step.get('uses', '').startswith('oven-sh/setup-bun@'))
        self.assertEqual(release_bun['with']['bun-version'], tools['bun'])
        self.assertNotIn('GH_TOKEN', self.jobs['release']['env'])

    def test_bun_projects_have_no_npm_lockfile(self):
        tracked = [Path(path) for path in subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT, text=True).split('\0') if path]
        projects = {path.parent for path in tracked if path.name == 'bun.lock'}
        self.assertIn(Path('apps/www'), projects)
        npm_locks = [str(path) for path in tracked if path.name in {'package-lock.json', 'npm-shrinkwrap.json'} and path.parent in projects]
        self.assertEqual(npm_locks, [])

    def test_validate_gates_on_every_validation_job(self):
        validate = self.jobs['validate']
        self.assertEqual(validate['needs'], ['checks', 'backend-tests'])
        self.assertEqual(validate['if'], '${{ !cancelled() }}')
        self.assertEqual(validate['outputs']['marketing'], '${{ needs.checks.outputs.marketing }}')
        gate = validate['steps'][-1]
        self.assertEqual(gate['env'], {'CHECKS': '${{ needs.checks.result }}', 'BACKEND_TESTS': '${{ needs.backend-tests.result }}'})
        self.assertIn('test "$CHECKS" = success', gate['run'])
        self.assertIn('test "$BACKEND_TESTS" = success', gate['run'])
        shards = self.jobs['backend-tests']['strategy']
        self.assertFalse(shards['fail-fast'])
        self.assertGreater(len(shards['matrix']['shard']), 1)
        for name in ['checks', 'backend-tests']:
            self.assertNotIn('dotnet test apps/backend.tests', json.dumps(self.jobs[name]))

    def test_scanned_artifact_is_promoted_without_rebuilding(self):
        image = self.jobs['image']
        self.assertNotIn('needs', image)
        steps = image['steps']
        scan = next(step for step in steps if step.get('uses', '').startswith('aquasecurity/trivy-action@'))
        self.assertEqual(scan['with']['exit-code'], '1')
        self.assertEqual(scan['with']['severity'], 'CRITICAL,HIGH')
        self.assertTrue(scan['with']['ignore-unfixed'])
        self.assertLess(next(i for i, step in enumerate(steps) if 'build-image.sh' in step.get('run', '')), steps.index(scan))
        self.assertLess(steps.index(scan), next(i for i, step in enumerate(steps) if step.get('uses', '').startswith('actions/upload-artifact@')))
        release = self.jobs['release']
        self.assertNotIn('build-image.sh', json.dumps(release))
        self.assertNotIn('bun run build', json.dumps(release))
        names = [step.get('name') for step in release['steps']]
        self.assertLess(names.index('Verify trusted main and publish the scanned image'), names.index('Deploy the tested backend revision and immutable image'))
        downloads = [step for step in release['steps'] if step.get('uses', '').startswith('actions/download-artifact@')]
        self.assertEqual({step['with']['name'] for step in downloads}, {'backend-${{ github.sha }}', 'marketing-${{ github.sha }}'})
        self.assertTrue(all('run-id' not in step['with'] and 'repository' not in step['with'] for step in downloads))

    def test_marketing_paths_and_manual_releases(self):
        spec = importlib.util.spec_from_file_location('marketing', ROOT / 'scripts/ci/marketing-changed.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        for path in ['apps/www/src/pages/index.astro', '.github/workflows/ci.yml', 'scripts/ci/send-release.sh', '.mise.toml', 'package.json', 'bun.lock', 'patches/dependency.patch']:
            self.assertTrue(module.selected([path]), path)
        self.assertFalse(module.selected([]))
        self.assertFalse(module.selected(['README.md', 'apps/backend/Program.cs']))
        for event, result in [('workflow_dispatch', 'true'), ('pull_request', 'false')]:
            actual = subprocess.check_output(['python3', str(ROOT / 'scripts/ci/marketing-changed.py')], env=dict(os.environ, GITHUB_EVENT_NAME=event), text=True)
            self.assertEqual(actual.strip(), 'changed=' + result)

    def test_release_compose_requires_exact_image_and_preserves_provider_flags(self):
        env = {'PATH': os.environ['PATH'], 'HOME': os.environ.get('HOME', ''), 'DOCKER_CONFIG': os.environ.get('DOCKER_CONFIG', ''), 'LOKI_URL': 'https://example.invalid', 'LOKI_USERNAME': 'fixture', 'GRAFANA_CLOUD_API_KEY': 'fixture'}
        base = subprocess.run(['docker', 'compose', '--env-file', '/dev/null', '-f', 'docker-compose.yml', 'config', '--format', 'json'], cwd=ROOT, env=env, capture_output=True, text=True, check=True, timeout=30)
        self.assertEqual(json.loads(base.stdout)['services']['backend']['image'], IMAGE + ':latest')
        command = ['docker', 'compose', '--env-file', '/dev/null', '-f', 'docker-compose.yml', '-f', 'scripts/ci/compose.release.yaml', 'config', '--format', 'json']
        result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        result = subprocess.run(command, cwd=ROOT, env=env | {'BACKEND_IMAGE': DIGEST}, capture_output=True, text=True, check=True, timeout=30)
        backend = json.loads(result.stdout)['services']['backend']
        self.assertEqual(backend['image'], DIGEST)
        self.assertEqual(backend['environment']['Highlights__Worker__AuthorizeLaunchCatchup'], 'false')


class ScriptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'commands.jsonl'
        self.env = {'PATH': str(self.bin) + os.pathsep + os.environ['PATH'], 'HOME': str(self.root), 'FIXTURE_SHA': SHA, 'FIXTURE_DIGEST': DIGEST, 'FIXTURE_LOG': str(self.log), 'GITHUB_ACTIONS': 'true', 'GITHUB_ACTOR': 'scowalt', 'GITHUB_REPOSITORY': 'scowalt/nailbiter', 'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REF': 'refs/heads/main', 'GITHUB_REF_PROTECTED': 'true', 'GITHUB_SHA': SHA, 'GITHUB_RUN_ID': '123', 'PRODUCTION_RELEASES_ENABLED': 'true'}
        self.executable('git', '#!/bin/sh\nprintf "%s\\n" "$FIXTURE_SHA"\n')
        self.executable('gh', '#!/bin/sh\nprintf "%s\\n" "${FIXTURE_MAIN:-$FIXTURE_SHA}"\n')
        self.executable('doppler', '#!/bin/sh\nshift 2\nexec "$@"\n')
        self.executable('docker', r'''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
sql = sys.stdin.read() if args[0] in {"exec", "login"} else ""
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps({"args": args, "sql": sql if args[0] == "exec" else "", "image": os.environ.get("BACKEND_IMAGE")}) + "\n")
if args[:2] == ["image", "inspect"]:
    print(os.environ["FIXTURE_DIGEST"] if "RepoDigests" in args[3] else "sha256:" + "c" * 64)
if args[0] == "inspect":
    print(os.environ.get("FIXTURE_RUNNING", "true") + " sha256:" + "c" * 64)
sys.exit(1 if "FAIL" in sql else 0)
''')
        self.executable('curl', '#!/bin/sh\nexit "${FIXTURE_HEALTH_FAIL:-0}"\n')
        (self.root / 'scripts/ci').mkdir(parents=True)
        (self.root / 'scripts/ensure-swap.sh').write_text('exit 0\n')
        (self.root / 'scripts/ci/require-release.sh').write_bytes((ROOT / 'scripts/ci/require-release.sh').read_bytes())
        self.migrations = self.root / 'apps/backend/Migrations'
        self.migrations.mkdir(parents=True)
        (self.root / '.ci').mkdir()

    def executable(self, name, text):
        file = self.bin / name
        file.write_text(text)
        file.chmod(0o755)

    def run_script(self, name, *args, env=None):
        return subprocess.run(['bash', str(ROOT / 'scripts/ci' / name), *args], cwd=self.root, env=self.env | (env or {}), capture_output=True, text=True, timeout=15)

    def commands(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_checkout_uses_exact_event_sha_including_pr_merge_commits(self):
        self.assertEqual(self.run_script('verify-checkout.sh', env={'GITHUB_EVENT_NAME': 'pull_request'}).returncode, 0)
        for sha in ['b' * 40, '', 'main', SHA + ';exit 0']:
            self.assertNotEqual(self.run_script('verify-checkout.sh', env={'GITHUB_SHA': sha}).returncode, 0)

    def test_release_guard_rejects_forks_prs_tags_unarmed_unprotected_and_stale_source(self):
        for overrides in [{'GITHUB_ACTIONS': 'false'}, {'GITHUB_REPOSITORY': 'fork/nailbiter'}, {'PRODUCTION_RELEASES_ENABLED': ''}, {'GITHUB_REF_PROTECTED': 'false'}, {'GITHUB_REF': 'refs/tags/v1'}, {'GITHUB_REF': 'refs/heads/feature'}, {'GITHUB_EVENT_NAME': 'pull_request'}, {'GITHUB_EVENT_NAME': 'pull_request_target'}, {'GITHUB_EVENT_NAME': 'workflow_run'}, {'GITHUB_EVENT_NAME': 'schedule'}, {'GITHUB_SHA': 'b' * 40}, {'FIXTURE_MAIN': 'b' * 40}]:
            with self.subTest(overrides=overrides):
                self.assertNotEqual(self.run_script('require-release.sh', env=overrides).returncode, 0)
        for event in ['push', 'workflow_dispatch']:
            self.assertEqual(self.run_script('require-release.sh', env={'GITHUB_EVENT_NAME': event}).returncode, 0)
        self.assertEqual(self.commands(), [])

    def test_exact_digest_and_legacy_migration_behavior(self):
        (self.migrations / '020_legacy.sql').write_text('FAIL legacy')
        (self.migrations / '021_new.sql').write_text('SELECT modern')
        result = self.run_script('deploy-backend.sh', SHA, DIGEST)
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        migrations = [command for command in commands if command['args'][0] == 'exec']
        self.assertEqual([command['sql'] for command in migrations], ['FAIL legacy', 'SELECT modern'])
        self.assertNotIn('ON_ERROR_STOP=1', migrations[0]['args'])
        self.assertIn('ON_ERROR_STOP=1', migrations[1]['args'])
        compose = [command for command in commands if command['args'][0] == 'compose']
        self.assertEqual(len(compose), 2)
        self.assertTrue(all(command['image'] == DIGEST for command in compose))
        self.assertEqual(compose[1]['args'][-6:], ['up', '-d', '--force-recreate', '--no-deps', 'backend', 'caddy'])
        self.assertLess(commands.index(migrations[1]), commands.index(compose[1]))

    def test_modern_migration_failure_prevents_restart_and_prune(self):
        (self.migrations / '021_failure.sql').write_text('FAIL modern')
        (self.migrations / '022_never.sql').write_text('SELECT never')
        self.assertNotEqual(self.run_script('deploy-backend.sh', SHA, DIGEST).returncode, 0)
        self.assertEqual(len(self.commands()), 2)
        self.assertFalse(any('up' in command['args'] or 'prune' in command['args'] for command in self.commands()))

    def test_failed_liveness_or_wrong_running_container_prevents_pruning(self):
        (self.migrations / '021_success.sql').write_text('SELECT modern')
        for failure in [{'FIXTURE_HEALTH_FAIL': '1'}, {'FIXTURE_RUNNING': 'false'}]:
            self.assertNotEqual(self.run_script('deploy-backend.sh', SHA, DIGEST, env=failure).returncode, 0)
        self.assertTrue(any('up' in command['args'] for command in self.commands()))
        self.assertFalse(any('prune' in command['args'] for command in self.commands()))

    def test_wrong_revision_and_mutable_or_missing_images_prevent_all_mutation(self):
        for sha, image in [('b' * 40, DIGEST), ('main', DIGEST), (SHA + ';exit 0', DIGEST), (SHA, IMAGE + ':latest'), (SHA, LEGACY_DIGEST), (SHA, DIGEST.replace('ghcr.io', 'ghcrXio')), (SHA, DIGEST + ';exit 0'), (SHA, 'other/image@sha256:' + 'b' * 64)]:
            self.assertNotEqual(self.run_script('deploy-backend.sh', sha, image).returncode, 0)
        self.assertNotEqual(self.run_script('deploy-backend.sh', SHA).returncode, 0)
        self.assertEqual(self.commands(), [])

    def prepare_image(self):
        import hashlib
        body = b'synthetic archive bytes'
        (self.root / '.ci/backend.tar').write_bytes(body)
        (self.root / '.ci/backend.sha256').write_text(hashlib.sha256(body).hexdigest() + '  .ci/backend.tar\n')
        (self.root / '.ci/backend.commit').write_text(SHA + '\n')
        (self.root / '.ci/backend.image-id').write_text('sha256:' + 'c' * 64 + '\n')

    def test_publisher_checks_bytes_identity_and_pushes_without_rebuilding(self):
        self.prepare_image()
        result = self.run_script('publish-image.sh', env={'GHCR_TOKEN': 'fixture-token'})
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        self.assertFalse(any(command['args'][0] == 'build' for command in commands))
        self.assertEqual(commands[0]['args'], ['load', '--input', '.ci/backend.tar'])
        self.assertIn(['login', 'ghcr.io', '--username', 'scowalt', '--password-stdin'], [command['args'] for command in commands])
        self.assertEqual((self.root / '.ci/backend-image.txt').read_text().strip(), DIGEST)
        self.assertEqual([command['args'][1] for command in commands if command['args'][0] == 'push'], [IMAGE + ':sha-' + SHA, IMAGE + ':latest'])
        self.assertNotIn('fixture-token', self.log.read_text())

    def test_publisher_rejects_foreign_digest_before_latest_promotion(self):
        self.prepare_image()
        for digest in [LEGACY_DIGEST, DIGEST.replace('ghcr.io', 'ghcrXio')]:
            with self.subTest(digest=digest):
                self.log.unlink(missing_ok=True)
                (self.root / '.ci/backend-image.txt').unlink(missing_ok=True)
                result = self.run_script('publish-image.sh', env={'GHCR_TOKEN': 'fixture-token', 'FIXTURE_DIGEST': digest})
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual([command['args'][1] for command in self.commands() if command['args'][0] == 'push'], [IMAGE + ':sha-' + SHA])
                self.assertFalse((self.root / '.ci/backend-image.txt').exists())

    def test_publisher_rejects_wrong_commit_or_corrupted_artifact_before_login(self):
        self.prepare_image()
        (self.root / '.ci/backend.commit').write_text('b' * 40)
        self.assertNotEqual(self.run_script('publish-image.sh', env={'GHCR_TOKEN': 'fixture-token'}).returncode, 0)
        self.assertEqual(self.commands(), [])
        (self.root / '.ci/backend.commit').write_text(SHA)
        (self.root / '.ci/backend.tar').write_bytes(b'changed')
        self.assertNotEqual(self.run_script('publish-image.sh', env={'GHCR_TOKEN': 'fixture-token'}).returncode, 0)
        self.assertEqual(self.commands(), [])

    def prepare_transport(self):
        (self.root / '.ci/backend-image.txt').write_text(DIGEST)
        self.executable('git', '#!/bin/sh\nif [ "$1" = bundle ]; then printf fixture-bundle > "$3"; else printf "%s\\n" "$FIXTURE_SHA"; fi\n')
        self.executable('tailscaled', '''#!/usr/bin/env python3
import socket, sys, time
path = next(arg.removeprefix("--socket=") for arg in sys.argv if arg.startswith("--socket="))
sock = socket.socket(socket.AF_UNIX)
sock.bind(path)
while True:
    time.sleep(1)
''')
        self.executable('tailscale', '''#!/usr/bin/env python3
import json, os, pathlib, shlex, subprocess, sys
if "up" in sys.argv:
    auth = next(arg for arg in sys.argv if arg.startswith("--auth-key="))
    assert auth.startswith("--auth-key=file:")
    path = pathlib.Path(auth.removeprefix("--auth-key=file:"))
    assert path.read_text() == "fixture-oauth?ephemeral=true&preauthorized=true"
    assert path.stat().st_mode & 0o777 == 0o600
    assert "TAILSCALE_OAUTH_SECRET" not in os.environ
    with open(os.environ["FIXTURE_LOG"], "a") as log:
        log.write(json.dumps({"keyfile": str(path)}) + "\\n")
if "ssh" in sys.argv:
    command = shlex.split(sys.argv[-1])
    assert command[:2] == ["bash", "-c"]
    subprocess.run(["bash", "-n", "-c", command[2]], check=True)
    assert sys.stdin.read() == "fixture-bundle"
    with open(os.environ["FIXTURE_LOG"], "a") as log:
        log.write(json.dumps({"remote": command[2]}) + "\\n")
''')

    def test_transport_rejects_historical_and_foreign_images_before_enrollment(self):
        self.prepare_transport()
        for image in [LEGACY_DIGEST, DIGEST.replace('ghcr.io', 'ghcrXio'), IMAGE + ':latest']:
            with self.subTest(image=image):
                self.log.unlink(missing_ok=True)
                (self.root / '.ci/backend-image.txt').write_text(image)
                result = self.run_script('send-release.sh', env={'TAILSCALE_OAUTH_SECRET': 'fixture-oauth'})
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.commands(), [])

    def test_bundle_transport_locks_remote_and_cleans_temporary_oauth_file(self):
        self.prepare_transport()
        result = self.run_script('send-release.sh', env={'TAILSCALE_OAUTH_SECRET': 'fixture-oauth'})
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        self.assertFalse(Path(next(command['keyfile'] for command in commands if 'keyfile' in command)).exists())
        remote = next(command['remote'] for command in commands if 'remote' in command)
        self.assertIn('git fetch "$bundle" HEAD', remote)
        self.assertIn('trap \'rm -f "$bundle"\' EXIT', remote)
        self.assertLess(remote.index('flock -w 600 9'), remote.index('git fetch'))
        self.assertLess(remote.index('git checkout'), remote.index('bash scripts/ci/deploy-backend.sh'))
        self.assertIn(SHA, remote)
        self.assertIn(DIGEST, remote)
        self.assertIn('git remote set-url origin https://github.com/scowalt/nailbiter.git', remote)
        self.assertNotIn('fixture-oauth', self.log.read_text())


if __name__ == '__main__':
    unittest.main()
