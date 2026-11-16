import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
SHA = 'a' * 40
IMAGE_ID = 'sha256:' + 'c' * 64
FOREIGN_IMAGE_ID = 'sha256:' + 'd' * 64
DIGEST = 'ghcr.io/scowalt/nailbiter/backend@sha256:' + 'b' * 64
EXPRESSION = re.compile(r'^\$\{\{ ([\w.-]+) \}\}$')
FAKE_LOGGED = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = [pathlib.Path(sys.argv[0]).name] + sys.argv[1:]
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps({"args": args, "cwd": os.getcwd()}) + "\n")
if args[1:3] == ["image", "inspect"]:
    print(os.environ["FIXTURE_DIGEST"] if "RepoDigests" in args[4] else os.environ["FIXTURE_IMAGE_ID"])
if args[0] == "bun" and "build" in args:
    dist = pathlib.Path(args[args.index("--cwd") + 1]) / "dist"
    dist.mkdir(parents=True)
    (dist / "index.html").write_text(os.environ["FIXTURE_SITE"])
'''


class ReleaseArtifactHashTests(unittest.TestCase):
    def setUp(self):
        self.jobs = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())['jobs']
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'commands.jsonl'
        for name in ['docker', 'bun', 'bunx']:
            self.executable(name, FAKE_LOGGED)
        self.executable('git', '#!/bin/sh\nprintf "%s\\n" "$GITHUB_SHA"\n')
        self.executable('gh', '#!/bin/sh\nprintf "%s\\n" "$GITHUB_SHA"\n')
        self.producer = self.workspace('producer')
        self.release = self.workspace('release')
        self.env = {'PATH': str(self.bin) + os.pathsep + os.environ['PATH'], 'HOME': str(self.root), 'FIXTURE_LOG': str(self.log),
                    'FIXTURE_DIGEST': DIGEST, 'FIXTURE_IMAGE_ID': IMAGE_ID, 'FIXTURE_SITE': 'built site', 'GITHUB_ACTIONS': 'true',
                    'GITHUB_ACTOR': 'scowalt', 'GITHUB_REPOSITORY': 'scowalt/nailbiter', 'GITHUB_EVENT_NAME': 'push',
                    'GITHUB_REF': 'refs/heads/main', 'GITHUB_REF_PROTECTED': 'true', 'GITHUB_SHA': SHA, 'PRODUCTION_RELEASES_ENABLED': 'true'}
        self.context = {'github.token': 'fixture-token', 'secrets.CLOUDFLARE_API_TOKEN': 'fixture-cloudflare'}

    def workspace(self, name):
        path = self.root / name
        (path / '.ci').mkdir(parents=True)
        (path / 'apps/www').mkdir(parents=True)
        (path / 'scripts/ci').mkdir(parents=True)
        for script in ['require-release.sh', 'publish-image.sh']:
            shutil.copy(ROOT / 'scripts/ci' / script, path / 'scripts/ci' / script)
        return path

    def executable(self, name, text):
        file = self.bin / name
        file.write_text(text)
        file.chmod(0o755)

    def commands(self):
        return [json.loads(line)['args'] for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def step(self, job, name):
        return next(step for step in self.jobs[job]['steps'] if step.get('name') == name)

    def resolve(self, value):
        match = EXPRESSION.match(str(value))
        return self.context.get(match.group(1), '') if match else str(value)

    def run_step(self, workspace, step, env=None):
        self.assertNotIn('shell', step)
        script = self.root / 'step.sh'
        script.write_text(step['run'])
        step_env = {name: self.resolve(value) for name, value in step.get('env', {}).items()}
        return subprocess.run(['bash', '-e', str(script)], cwd=workspace, env=self.env | step_env | (env or {}),
                              capture_output=True, text=True, timeout=30)

    def produce(self, job, step_name):
        step = self.step(job, step_name)
        output = self.root / f'{job}.output'
        result = self.run_step(self.producer, step, env={'GITHUB_OUTPUT': str(output)})
        self.assertEqual(result.returncode, 0, result.stderr)
        step_outputs = dict(line.split('=', 1) for line in output.read_text().splitlines()) if output.exists() else {}
        for key, value in step_outputs.items():
            self.context[f"steps.{step.get('id')}.outputs.{key}"] = value
        self.publish_outputs(job)
        self.context = {key: value for key, value in self.context.items() if not key.startswith('steps.')}

    def publish_outputs(self, job):
        for key, expression in self.jobs[job].get('outputs', {}).items():
            self.context[f'needs.{job}.outputs.{key}'] = self.resolve(expression)

    def upload_paths(self, artifact):
        uploads = [step for job in self.jobs.values() for step in job['steps']
                   if step.get('uses', '').startswith('actions/upload-artifact@') and step['with']['name'] == artifact]
        self.assertEqual(len(uploads), 1, artifact)
        return uploads[0]['with']['path'].split()

    def transfer(self, artifact):
        for path in self.upload_paths(artifact):
            shutil.copy(self.producer / path, self.release / path)

    def produce_image(self):
        (self.producer / '.ci/backend.tar').write_bytes(b'scanned image archive')
        self.produce('image', 'Bind the scanned image to this revision')
        self.transfer('backend-${{ github.sha }}')
        self.log.unlink(missing_ok=True)

    def produce_marketing(self):
        self.produce('checks', 'Build and package marketing once')
        self.publish_outputs('validate')
        self.transfer('marketing-${{ github.sha }}')
        self.log.unlink(missing_ok=True)

    def publish(self, image_id=IMAGE_ID):
        return self.run_step(self.release, self.step('release', 'Verify trusted main and publish the scanned image'), env={'FIXTURE_IMAGE_ID': image_id})

    def deploy_marketing(self):
        return self.run_step(self.release, self.step('release', 'Deploy the tested marketing artifact'))

    def replace_artifact(self, name, body):
        (self.release / f'.ci/{name}.tar').write_bytes(body)
        (self.release / f'.ci/{name}.sha256').write_text(f'{hashlib.sha256(body).hexdigest()}  .ci/{name}.tar\n')

    def test_every_artifact_the_release_downloads_carries_only_its_archive_and_commit(self):
        downloads = [step['with']['name'] for step in self.jobs['release']['steps'] if step.get('uses', '').startswith('actions/download-artifact@')]
        self.assertEqual(len(downloads), 3)
        for artifact in downloads:
            with self.subTest(artifact=artifact):
                self.assertEqual(sorted(Path(path).suffix for path in self.upload_paths(artifact)), ['.commit', '.tar'])

    def test_the_release_publishes_the_image_whose_hash_and_id_the_image_job_reported(self):
        self.produce_image()
        result = self.publish()
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.commands()
        self.assertEqual(commands[0], ['docker', 'load', '--input', '.ci/backend.tar'])
        self.assertEqual([command[2] for command in commands if command[1] == 'push'],
                         ['ghcr.io/scowalt/nailbiter/backend:sha-' + SHA, 'ghcr.io/scowalt/nailbiter/backend:latest'])

    def test_a_replaced_or_corrupted_image_archive_is_never_loaded(self):
        for tamper in ['corrupted', 'replaced with matching hash and id files']:
            with self.subTest(tamper=tamper):
                self.setUp()
                self.produce_image()
                if tamper == 'corrupted':
                    with open(self.release / '.ci/backend.tar', 'ab') as archive:
                        archive.write(b'x')
                else:
                    self.replace_artifact('backend', b'foreign image archive')
                    (self.release / '.ci/backend.image-id').write_text(FOREIGN_IMAGE_ID + '\n')
                result = self.publish(image_id=FOREIGN_IMAGE_ID)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.commands(), [])

    def test_a_loaded_image_that_is_not_the_scanned_image_is_never_pushed(self):
        self.produce_image()
        (self.release / '.ci/backend.image-id').write_text(FOREIGN_IMAGE_ID + '\n')
        result = self.publish(image_id=FOREIGN_IMAGE_ID)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual([command[1] for command in self.commands()], ['load', 'image'])

    def test_the_release_deploys_the_marketing_site_whose_hash_the_checks_job_reported(self):
        self.produce_marketing()
        result = self.deploy_marketing()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.release / 'apps/www/dist/index.html').read_text(), 'built site')
        self.assertEqual(self.commands()[-1], ['bunx', 'wrangler', 'deploy'])

    def test_a_replaced_or_corrupted_marketing_archive_is_never_unpacked_or_deployed(self):
        for tamper in ['corrupted', 'replaced with a matching hash file']:
            with self.subTest(tamper=tamper):
                self.setUp()
                self.produce_marketing()
                if tamper == 'corrupted':
                    with open(self.release / '.ci/marketing.tar', 'ab') as archive:
                        archive.write(b'x')
                else:
                    staging = self.root / 'foreign'
                    (staging / 'dist').mkdir(parents=True)
                    (staging / 'dist/index.html').write_text('foreign site')
                    subprocess.run(['tar', '-C', str(staging), '-cf', str(self.root / 'foreign.tar'), 'dist'], check=True)
                    self.replace_artifact('marketing', (self.root / 'foreign.tar').read_bytes())
                result = self.deploy_marketing()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.commands(), [])
                self.assertFalse((self.release / 'apps/www/dist').exists())

    def test_a_missing_job_output_stops_the_release_step_and_names_the_output(self):
        for artifact, produce, consume, output in [('backend', self.produce_image, self.publish, 'needs.image.outputs.sha256'),
                                                   ('backend', self.produce_image, self.publish, 'needs.image.outputs.image-id'),
                                                   ('marketing', self.produce_marketing, self.deploy_marketing, 'needs.validate.outputs.marketing-sha256')]:
            with self.subTest(output=output):
                self.setUp()
                produce()
                self.assertRegex(self.context.get(output, ''), r'^(sha256:)?[0-9a-f]{64}$')
                self.context[output] = ''
                result = consume()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('output is required', result.stderr)
                self.assertEqual(self.commands(), [])
                self.assertFalse((self.release / 'apps/www/dist').exists())


if __name__ == '__main__':
    unittest.main()
