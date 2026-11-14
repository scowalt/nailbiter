from contextlib import contextmanager
import http.client
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
TAILNET_URL = 'https://box.example-tailnet.ts.net:8494'
TAILNET_HOST = 'box.example-tailnet.ts.net:8494'
OTHER_TAILNET_HOST = 'other.example-tailnet.ts.net'


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def status(port, host):
    connection = http.client.HTTPConnection('127.0.0.1', port, timeout=30)
    try:
        connection.request('GET', '/', headers={'Host': host})
        return connection.getresponse().status
    finally:
        connection.close()


class MarketingDevHostsTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.site = Path(cls.tmp.name) / 'www'
        tracked = subprocess.run(['git', 'ls-files', '-z', 'apps/www'], cwd=ROOT, check=True, capture_output=True, text=True).stdout
        for name in filter(None, tracked.split('\0')):
            target = cls.site / Path(name).relative_to('apps/www')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
        subprocess.run(['bun', 'install', '--frozen-lockfile', '--ignore-scripts'], cwd=cls.site, check=True, capture_output=True)
        cls.env = {key: value for key, value in os.environ.items() if not key.startswith('PORTLESS')}
        cls.env.update(CI='1', ASTRO_TELEMETRY_DISABLED='1')

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def astro(self, *args, **env):
        return [str(self.site / 'node_modules/.bin/astro'), *args], {**self.env, **env}

    @contextmanager
    def serve(self, command, **env):
        port = free_port()
        args, child_env = self.astro(command, '--port', str(port), '--host', '127.0.0.1', **env)
        log = tempfile.TemporaryFile()
        server = subprocess.Popen(args, cwd=self.site, env=child_env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 90
            while True:
                try:
                    status(port, f'localhost:{port}')
                    break
                except OSError:
                    if server.poll() is not None or time.monotonic() > deadline:
                        log.seek(0)
                        self.fail(f'astro {command} did not start: {log.read().decode(errors="replace")}')
                    time.sleep(0.25)
            yield port
        finally:
            os.killpg(server.pid, 15)
            server.wait(timeout=30)
            log.close()

    def test_dev_serves_the_tailnet_host_portless_shares(self):
        with self.serve('dev', PORTLESS_TAILSCALE_URL=TAILNET_URL) as port:
            self.assertEqual(200, status(port, f'localhost:{port}'))
            self.assertEqual(200, status(port, TAILNET_HOST))
            self.assertEqual(403, status(port, OTHER_TAILNET_HOST))

    def test_dev_without_tailscale_sharing_blocks_tailnet_hosts(self):
        with self.serve('dev') as port:
            self.assertEqual(403, status(port, TAILNET_HOST))

    def test_preview_keeps_blocking_the_tailnet_host(self):
        args, env = self.astro('build', PORTLESS_TAILSCALE_URL=TAILNET_URL)
        subprocess.run(args, cwd=self.site, env=env, check=True, capture_output=True)
        with self.serve('preview', PORTLESS_TAILSCALE_URL=TAILNET_URL) as port:
            self.assertEqual(200, status(port, f'localhost:{port}'))
            self.assertEqual(403, status(port, TAILNET_HOST))


if __name__ == '__main__':
    unittest.main()
