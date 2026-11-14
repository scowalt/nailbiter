from contextlib import contextmanager, suppress
import http.client
import os
from pathlib import Path
import re
import shutil
import signal
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


def output(log):
    log.seek(0)
    return re.sub(r'\x1b\[[0-9;]*m', '', log.read().decode(errors='replace'))


def interrupt(signum, frame):
    raise KeyboardInterrupt


class MarketingDevHostsTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        previous_sigterm = signal.signal(signal.SIGTERM, interrupt)
        cls.addClassCleanup(signal.signal, signal.SIGTERM, previous_sigterm)
        cls.tmp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.tmp.cleanup)
        cls.site = Path(cls.tmp.name) / 'www'
        tracked = subprocess.run(['git', 'ls-files', '-z', 'apps/www'], cwd=ROOT, check=True, capture_output=True, text=True).stdout
        for name in filter(None, tracked.split('\0')):
            target = cls.site / Path(name).relative_to('apps/www')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
        subprocess.run(['bun', 'install', '--frozen-lockfile', '--ignore-scripts'], cwd=cls.site, check=True, capture_output=True)
        cls.env = {key: value for key, value in os.environ.items() if not key.startswith('PORTLESS')}
        cls.env.update(CI='1', ASTRO_TELEMETRY_DISABLED='1')

    def astro(self, *args, **env):
        return [str(self.site / 'node_modules/.bin/astro'), *args], {**self.env, **env}

    @contextmanager
    def launch(self, command, **env):
        port = free_port()
        args, child_env = self.astro(command, '--port', str(port), '--host', '127.0.0.1', **env)
        with tempfile.TemporaryFile() as log:
            server = subprocess.Popen(args, cwd=self.site, env=child_env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                yield server, port, log
            finally:
                with suppress(ProcessLookupError):
                    os.killpg(server.pid, signal.SIGTERM)
                server.wait(timeout=30)

    def started(self, server, port):
        deadline = time.monotonic() + 90
        while server.poll() is None and time.monotonic() < deadline:
            try:
                status(port, f'localhost:{port}')
                return True
            except OSError:
                time.sleep(0.25)
        return False

    @contextmanager
    def serve(self, command, **env):
        with self.launch(command, **env) as (server, port, log):
            if not self.started(server, port):
                self.fail(f'astro {command} did not start: {output(log)}')
            yield port

    def dev_startup_error(self, tailscale_url):
        with self.launch('dev', PORTLESS_TAILSCALE_URL=tailscale_url) as (server, port, log):
            self.assertFalse(self.started(server, port), f'astro dev served with PORTLESS_TAILSCALE_URL={tailscale_url}')
            self.assertNotIn(server.returncode, (None, 0))
            return output(log)

    def test_dev_serves_the_tailnet_host_portless_shares(self):
        with self.serve('dev', PORTLESS_TAILSCALE_URL=TAILNET_URL) as port:
            self.assertEqual(200, status(port, f'localhost:{port}'))
            self.assertEqual(200, status(port, TAILNET_HOST))
            self.assertEqual(403, status(port, OTHER_TAILNET_HOST))

    def test_dev_refuses_a_tailnet_url_without_an_exact_hostname(self):
        for tailscale_url in ['https://.ts.net:8494', 'file:///tmp']:
            with self.subTest(tailscale_url=tailscale_url):
                self.dev_startup_error(tailscale_url)

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
