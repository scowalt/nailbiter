import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import tomllib
import unittest

ROOT = Path(__file__).resolve().parents[2]
PINS = tomllib.loads((ROOT / '.mise.toml').read_text())['tools']
SDK = json.loads((ROOT / 'apps/backend/global.json').read_text())['sdk']['version']
SYSTEM_TOOLS = ['awk', 'bash', 'dirname', 'git', 'grep', 'head', 'sed', 'sleep', 'sort', 'tail', 'timeout']
GLOBAL_DEFAULTS = ['.gitconfig', '.config/git/config', '.config/mise/config.toml', '.tool-versions', '.local/state/mise/trusted-configs']


def says(line, status=0):
    return f"printf '%s\\n' '{line}'\nexit {status}\n"


def pinned_tool_shims():
    shims = {name: says(f'{name} {version}') for name, version in PINS.items() if 'dotnet' not in name}
    shims['dotnet'] = says(SDK)
    return shims


def write_executable(path, body):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text('#!/bin/sh\n' + body)
    path.chmod(0o755)


def global_defaults():
    home = Path.home()
    state = {}
    for name in GLOBAL_DEFAULTS:
        path = home / name
        state[name] = sorted(os.listdir(path)) if path.is_dir() else path.read_bytes() if path.exists() else None
    return state


def tree_state(*roots):
    state = {}
    for root in roots:
        for path in sorted(root.rglob('*')):
            if path.is_file() and not path.is_symlink():
                info = path.stat()
                state[str(path)] = (info.st_size, info.st_mtime_ns)
    return state


def git(*args, cwd):
    return subprocess.run(['git', *args], cwd=cwd, check=True, capture_output=True, text=True).stdout


def run_hook(worktree, script='.bb-env-setup.sh'):
    return subprocess.run(['env', '-i', f'HOME={Path.home()}', f'PATH={os.environ["PATH"]}', 'bash', script], cwd=worktree, capture_output=True, text=True, timeout=900)


class FreshWorktreeSetupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = Path(tempfile.mkdtemp(prefix='nailbiter-setup-'))
        cls.addClassCleanup(shutil.rmtree, cls.temp)
        cls.repo = cls.temp / 'repo'
        cls.worktree = cls.temp / 'worktree'
        sha = git('rev-parse', 'HEAD', cwd=ROOT).strip()
        git('clone', '--quiet', '--shared', '--no-checkout', str(ROOT), str(cls.repo), cwd=cls.temp)
        git('worktree', 'add', '--quiet', '--detach', str(cls.worktree), sha, cwd=cls.repo)
        git('worktree', 'add', '--quiet', '-b', 'other-feature', str(cls.temp / 'other'), sha, cwd=cls.repo)
        for name in ['scripts/setup.sh', '.bb-env-setup.sh']:
            shutil.copy2(ROOT / name, cls.worktree / name)
        cls.defaults_before = global_defaults()
        cls.refs_before = git('for-each-ref', cwd=cls.repo) + git('worktree', 'list', '--porcelain', cwd=cls.repo)
        cls.first = run_hook(cls.worktree)
        cls.after_first = tree_state(cls.worktree, cls.repo / '.git/hooks')
        cls.second = run_hook(cls.worktree)
        cls.after_second = tree_state(cls.worktree, cls.repo / '.git/hooks')
        cls.standalone = subprocess.run(['bash', 'scripts/setup.sh'], cwd=cls.worktree, capture_output=True, text=True, timeout=900)

    def test_bb_hook_makes_a_fresh_worktree_ready(self):
        self.assertEqual(self.first.returncode, 0, self.first.stdout + self.first.stderr)
        self.assertIn('READY (', self.first.stdout)
        self.assertNotIn('fail ', self.first.stdout)
        self.assertIn(f'ok    .NET SDK {SDK} (apps/backend/global.json)', self.first.stdout)
        self.assertIn('ok    git pre-commit hook installed (lefthook)', self.first.stdout)
        for project in ['.', 'apps/web-components', 'apps/www']:
            self.assertTrue((self.worktree / project / 'node_modules').is_dir(), project)
        self.assertTrue((self.worktree / 'apps/backend.tests/obj/project.assets.json').is_file())
        self.assertIn('lefthook', (self.repo / '.git/hooks/pre-commit').read_text())

    def test_repeated_setup_changes_nothing(self):
        self.assertEqual(self.second.returncode, 0, self.second.stdout + self.second.stderr)
        self.assertIn('ok    git pre-commit hook (lefthook)', self.second.stdout)
        self.assertEqual(self.after_first, self.after_second)

    def test_setup_runs_on_its_own_with_the_same_verdict(self):
        self.assertEqual(self.standalone.returncode, 0, self.standalone.stdout + self.standalone.stderr)
        self.assertIn('READY (', self.standalone.stdout)

    def test_setup_leaves_global_defaults_branches_and_refs_alone(self):
        self.assertEqual(global_defaults(), self.defaults_before)
        self.assertEqual(git('for-each-ref', cwd=self.repo) + git('worktree', 'list', '--porcelain', cwd=self.repo), self.refs_before)
        self.assertEqual(git('rev-parse', '--abbrev-ref', 'HEAD', cwd=self.temp / 'other').strip(), 'other-feature')


class ToolchainPreflightTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix='nailbiter-preflight-'))
        self.addCleanup(shutil.rmtree, self.root)
        for name in ['scripts/setup.sh', '.bb-env-setup.sh', '.mise.toml', 'apps/backend/global.json']:
            (self.root / name).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, self.root / name)
        git('init', '--quiet', cwd=self.root)
        self.bin = self.root / 'fake-bin'
        self.system = self.bin / 'system'
        self.system.mkdir(parents=True)
        for tool in SYSTEM_TOOLS:
            (self.system / tool).symlink_to(shutil.which(tool))

    def run_setup(self, tools, shell_tools=None, mise=True, script='scripts/setup.sh', env=()):
        tool_dir = self.bin / 'tools'
        for name, body in tools.items():
            write_executable(tool_dir / name, body)
        path = f'{tool_dir}:{self.system}'
        if shell_tools is not None:
            for name, body in shell_tools.items():
                write_executable(self.bin / 'shell' / name, body)
            path = f'{self.bin / "shell"}:{path}'
        if mise:
            body = mise if isinstance(mise, str) else f'[ "$1" = env ] && printf "export PATH=%s\\n" "{tool_dir}:{self.system}"\nexit 0\n'
            write_executable(self.bin / 'mise' / 'mise', body)
            path = f'{self.bin / "mise"}:{path}'
        return subprocess.run(['env', '-i', f'HOME={self.root}', f'PATH={path}', *env, 'bash', script], cwd=self.root, capture_output=True, text=True, timeout=120)

    def assertNotReady(self, result, *lines):
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('NOT READY', result.stdout)
        self.assertIn('skip  dependencies', result.stdout)
        for line in lines:
            self.assertIn(line, result.stdout)

    def test_matching_fake_toolchain_is_ready(self):
        result = self.run_setup(pinned_tool_shims())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('READY (0 warning(s))', result.stdout)

    def test_missing_mise_is_not_ready(self):
        result = self.run_setup(pinned_tool_shims(), mise=False)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('fail  mise is not on PATH', result.stdout)
        self.assertIn('https://mise.jdx.dev', result.stdout)

    def test_mise_that_cannot_load_the_config_is_not_ready(self):
        mise = 'echo "mise ERROR error parsing config file: /x/.mise.toml" >&2\necho "mise ERROR Config files in /x/.mise.toml are not trusted." >&2\nexit 1\n'
        result = self.run_setup(pinned_tool_shims(), mise=mise)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('      | mise ERROR Config files in /x/.mise.toml are not trusted.\nfail  mise could not load .mise.toml\n', result.stdout)
        self.assertIn("run 'mise trust'", result.stdout)

    def test_mise_warnings_are_not_run_as_shell_code(self):
        tool_dir = self.bin / 'tools'
        mise = f'[ "$1" = env ] && {{ echo "mise WARN it\'s time to update" >&2; printf "export PATH=%s\\n" "{tool_dir}:{self.system}"; }}\nexit 0\n'
        result = self.run_setup(pinned_tool_shims(), mise=mise)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, '')

    def test_pins_with_comments_and_prefixes_are_checked(self):
        (self.root / '.mise.toml').write_text(
            '[tools]\n'
            '# node = "22"\n'
            f'"asdf:mise-plugins/mise-dotnet-core" = "{SDK}"\n'
            'bun = "1.4" # minor line only\n'
            'uv = { version = "0.11.32" }\n'
            '\n[env]\nX = "1"\n')
        result = self.run_setup({'bun': says('1.4.2'), 'dotnet': says(SDK), 'uv': says('uv 0.11.32')})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('ok    bun 1.4\n', result.stdout)
        self.assertIn(f'ok    .NET SDK {SDK}', result.stdout)
        self.assertIn('warn  cannot check the uv pin in .mise.toml', result.stdout)
        self.assertNotIn('node', result.stdout)
        self.assertNotIn(' X ', result.stdout)

    def test_prefix_pin_does_not_match_a_longer_number(self):
        (self.root / '.mise.toml').write_text(f'[tools]\nbun = "1.4"\n"asdf:mise-plugins/mise-dotnet-core" = "{SDK}"\n')
        result = self.run_setup({'bun': says('1.40.0'), 'dotnet': says(SDK)})
        self.assertNotReady(result, 'does not provide bun 1.4')

    def test_missing_pinned_tool_is_not_ready(self):
        tools = pinned_tool_shims()
        del tools['bun']
        result = self.run_setup(tools)
        self.assertNotReady(result, f'fail  bun {PINS["bun"]} is not installed', 'fix: mise install')

    def test_mismatched_tool_version_is_not_ready(self):
        tools = pinned_tool_shims() | {'uv': says('uv 0.0.1')}
        result = self.run_setup(tools)
        self.assertNotReady(result, f'does not provide uv {PINS["uv"]}', 'fix: mise install')

    def test_version_printed_on_stderr_is_not_trusted(self):
        bun = f'echo "mise bun@{PINS["bun"]} [1/3] install" >&2\n' + says('1.3.11')
        result = self.run_setup(pinned_tool_shims() | {'bun': bun})
        self.assertNotReady(result, f'does not provide bun {PINS["bun"]}', 'fix: mise install')

    def test_tool_whose_version_check_fails_is_not_ready(self):
        result = self.run_setup(pinned_tool_shims() | {'bun': says(f'bun {PINS["bun"]}', 1)})
        self.assertNotReady(result, f'does not provide bun {PINS["bun"]}', 'fix: mise install')

    def test_dotnet_without_the_global_json_sdk_is_not_ready(self):
        tools = pinned_tool_shims() | {'dotnet': says('A compatible .NET SDK was not found.', 155)}
        result = self.run_setup(tools)
        self.assertNotReady(result, f'does not provide .NET SDK {SDK} (apps/backend/global.json)')

    def test_dotnet_from_a_later_major_is_not_ready(self):
        major = int(SDK.split('.')[0])
        tools = pinned_tool_shims() | {'dotnet': says(f'{major + 1}.0.100')}
        result = self.run_setup(tools)
        self.assertNotReady(result, f'does not provide .NET SDK {SDK}')

    def test_dotnet_older_feature_band_is_not_ready(self):
        major, minor, _ = SDK.split('.')
        tools = pinned_tool_shims() | {'dotnet': says(f'{major}.{minor}.100')}
        result = self.run_setup(tools)
        self.assertNotReady(result, f'does not provide .NET SDK {SDK}')

    def test_dotnet_later_feature_band_satisfies_latest_feature_roll_forward(self):
        major, minor, _ = SDK.split('.')
        tools = pinned_tool_shims() | {'dotnet': says(f'{major}.{minor}.999')}
        result = self.run_setup(tools)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn(f'ok    .NET SDK {SDK} (apps/backend/global.json)', result.stdout)

    def test_mise_and_global_json_disagreeing_is_not_ready(self):
        global_json = self.root / 'apps/backend/global.json'
        global_json.write_text(json.dumps({'sdk': {'version': '10.0.100', 'rollForward': 'latestFeature'}}))
        result = self.run_setup(pinned_tool_shims())
        self.assertNotReady(result, f'.mise.toml pins .NET SDK {SDK} but apps/backend/global.json requires 10.0.100')

    def test_failed_dependency_install_is_not_ready_and_shows_its_output(self):
        bun = f'[ "$1" = install ] && [ "$3" = . ] && {{ echo "error: lockfile had changes, but lockfile is frozen"; exit 1; }}\n' + says(PINS['bun'])
        result = self.run_setup(pinned_tool_shims() | {'bun': bun})
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('      | error: lockfile had changes, but lockfile is frozen\nfail  bun dependencies (.)', result.stdout)
        self.assertIn('ok    bun dependencies (apps/www)', result.stdout)
        self.assertIn('NOT READY: 1 problem(s)', result.stdout)

    def test_missing_playwright_browser_is_a_warning(self):
        bun = '[ "$1" = -e ] && exit 1\n' + says(PINS['bun'])
        result = self.run_setup(pinned_tool_shims() | {'bun': bun})
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn('warn  Playwright Chromium does not launch', result.stdout)
        self.assertIn('fix: mise exec -- bunx playwright install chromium', result.stdout)

    def test_bb_hook_keeps_a_worktree_that_is_not_ready(self):
        tools = pinned_tool_shims()
        del tools['dotnet']
        result = self.run_setup(tools, script='.bb-env-setup.sh')
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn(f'fail  .NET SDK {SDK} (apps/backend/global.json) is not installed', result.stdout)
        self.assertIn('NOT READY', result.stdout)
        self.assertIn('The worktree is kept', result.stdout)

    def test_bb_hook_stops_setup_at_its_deadline_and_keeps_the_worktree(self):
        bun = '[ "$1" = install ] && exec sleep 30\n' + says(PINS['bun'])
        started = time.monotonic()
        result = self.run_setup(pinned_tool_shims() | {'bun': bun}, script='.bb-env-setup.sh', env=['NAILBITER_SETUP_DEADLINE=2'])
        self.assertLess(time.monotonic() - started, 20)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn('did not finish before the provisioning deadline. The worktree is kept', result.stdout)

    def test_shadowed_tool_in_the_callers_shell_is_a_warning(self):
        result = self.run_setup(pinned_tool_shims(), shell_tools={'bun': says('0.0.1')})
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn(f"warn  'bun' in your own shell is {self.bin / 'shell' / 'bun'}, not bun {PINS['bun']}", result.stdout)
        self.assertIn("fix: run it as 'mise exec -- bun ...'", result.stdout)
        self.assertIn('READY (1 warning(s))', result.stdout)


if __name__ == '__main__':
    unittest.main()


class ConnectionCounter:
    def __init__(self):
        self.server = socket.create_server(('127.0.0.1', 0))
        self.connections = 0
        self.thread = threading.Thread(target=self.serve, daemon=True)
        self.thread.start()

    @property
    def url(self):
        return f'http://127.0.0.1:{self.server.getsockname()[1]}'

    def serve(self):
        while True:
            try:
                connection, _ = self.server.accept()
            except OSError:
                return
            self.connections += 1
            connection.close()

    def close(self):
        self.server.close()
        self.thread.join(5)


@unittest.skipUnless(shutil.which('mise'), 'needs a real mise')
class RealMiseShimTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix='nailbiter-mise-shims-'))
        self.addCleanup(shutil.rmtree, self.root)
        self.repo = self.root / 'repo'
        (self.repo / 'scripts').mkdir(parents=True)
        (self.repo / 'apps/backend').mkdir(parents=True)
        shutil.copy2(ROOT / 'scripts/setup.sh', self.repo / 'scripts/setup.sh')
        (self.repo / '.mise.toml').write_text('[tools]\nbun = "1.3.13"\n')
        git('init', '--quiet', cwd=self.repo)
        self.mise_dirs = {name: self.root / name.split('_')[1].lower() for name in ['MISE_DATA_DIR', 'MISE_CONFIG_DIR', 'MISE_CACHE_DIR', 'MISE_STATE_DIR']}
        (self.mise_dirs['MISE_CONFIG_DIR']).mkdir()
        (self.mise_dirs['MISE_CONFIG_DIR'] / 'config.toml').write_text('[settings]\nauto_install = true\nexec_auto_install = true\n')
        write_executable(self.installs / '1.0.0/bin/bun', "echo 1.0.0\n")
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        (self.bin / 'mise').symlink_to(os.path.realpath(shutil.which('mise')))
        for tool in SYSTEM_TOOLS:
            (self.bin / tool).symlink_to(shutil.which(tool))
        self.network = ConnectionCounter()
        self.addCleanup(self.network.close)
        subprocess.run(self.env(f'{self.bin}') + ['mise', 'reshim'], cwd=self.root, check=True, capture_output=True, timeout=60)

    @property
    def installs(self):
        return self.mise_dirs['MISE_DATA_DIR'] / 'installs/bun'

    def env(self, path):
        proxies = [f'{name}={self.network.url}' for name in ['HTTPS_PROXY', 'HTTP_PROXY', 'https_proxy', 'http_proxy', 'ALL_PROXY']]
        return ['env', '-i', f'HOME={self.root}', f'PATH={path}', *[f'{k}={v}' for k, v in self.mise_dirs.items()], *proxies]

    def test_pinned_version_missing_behind_a_shim_fails_without_installing(self):
        shims = self.mise_dirs['MISE_DATA_DIR'] / 'shims'
        self.assertTrue((shims / 'bun').exists())
        result = subprocess.run(self.env(f'{shims}:{self.bin}') + ['bash', 'scripts/setup.sh'], cwd=self.repo, capture_output=True, text=True, timeout=120)
        self.assertEqual(self.network.connections, 0, result.stdout + result.stderr)
        self.assertEqual(sorted(path.name for path in self.installs.iterdir()), ['1.0.0'])
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertNotIn('ok    bun', result.stdout)
        self.assertRegex(result.stdout, r'fail  .*bun 1\.3\.13.*\n      fix: mise install\n')
