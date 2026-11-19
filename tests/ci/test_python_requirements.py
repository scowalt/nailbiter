from pathlib import Path
import itertools
import re
import shlex
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
REQUIREMENT = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._-]*==[0-9][A-Za-z0-9.+!-]*$')
HASH = re.compile(r'^--hash=sha256:[0-9a-f]{64}$')
INSTALLERS = {'uvx', 'pipx', 'pip-sync', 'piptools'}
PIP = re.compile(r'pip[0-9.]*')
SEPARATORS = {'&&', '||', ';', '|'}
ASSIGNMENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*=.*', re.DOTALL)
KEYWORDS = {'if', 'then', 'elif', 'else', 'while', 'until', 'do', '!', '{'}
WRAPPERS = {
    'timeout': ({'-k', '--kill-after', '-s', '--signal'}, 1),
    'env': ({'-a', '--argv0', '-C', '--chdir', '-f', '--file', '-S', '--split-string', '-u', '--unset'}, 0),
    'nice': ({'-n', '--adjustment'}, 0),
    'nohup': (set(), 0),
    'sudo': ({'-C', '--close-from', '-D', '--chdir', '-g', '--group', '-p', '--prompt', '-R', '--chroot', '-r', '--role', '-T', '--command-timeout', '-t', '--type', '-U', '--other-user', '-u', '--user'}, 0),
    'time': (set(), 0),
    'exec': ({'-a'}, 0),
    'command': (set(), 0),
}
RUN_OPTIONS_WITH_VALUES = {
    '--allow-insecure-host', '--cache-dir', '--color', '--config-file', '--config-setting', '--config-settings-package',
    '--default-index', '--directory', '--env-file', '--exclude-newer', '--exclude-newer-package', '--extra',
    '--extra-index-url', '--find-links', '--fork-strategy', '--group', '--index', '--index-strategy', '--index-url',
    '--keyring-provider', '--link-mode', '--no-binary-package', '--no-build-isolation-package', '--no-build-package',
    '--no-editable-package', '--no-extra', '--no-group', '--no-sources-package', '--only-group', '--package',
    '--prerelease', '--project', '--python', '--python-platform', '--refresh-package', '--reinstall-package',
    '--resolution', '--upgrade-group', '--upgrade-package', '--with', '--with-editable', '--with-requirements',
    '-C', '-P', '-f', '-i', '-p', '-w',
}
RUN_FLAGS = {
    '--active', '--all-extras', '--all-groups', '--all-packages', '--compile-bytecode', '--exact', '--frozen',
    '--gui-script', '--help', '--isolated', '--locked', '--managed-python', '--module', '--no-binary', '--no-build',
    '--no-build-isolation', '--no-cache', '--no-config', '--no-default-groups', '--no-dev', '--no-editable',
    '--no-env-file', '--no-index', '--no-managed-python', '--no-progress', '--no-project', '--no-python-downloads',
    '--no-sources', '--no-sync', '--offline', '--only-dev', '--quiet', '--refresh', '--reinstall', '--script',
    '--system-certs', '--upgrade', '--verbose', '-U', '-h', '-m', '-n', '-q', '-s', '-v',
}
SCRIPT_FLAGS = {'--script', '-s', '--gui-script'}
INLINE_METADATA = re.compile(r'^# /// script\s*$', re.MULTILINE)
MIN_HASHED_FILES = 3


def logical_lines(text):
    return re.sub(r'\\\n', ' ', text).splitlines()


def ci_sources():
    for workflow in sorted((ROOT / '.github/workflows').glob('*.y*ml')):
        for job_name, job in yaml.safe_load(workflow.read_text())['jobs'].items():
            for index, step in enumerate(job.get('steps', [])):
                if 'run' in step:
                    yield f'{workflow.name}:{job_name}[{index}]', step['run']
    for script in sorted((ROOT / 'scripts/ci').iterdir()):
        if script.is_file():
            yield str(script.relative_to(ROOT)), script.read_text(errors='replace')


def local_sources():
    for hook, config in yaml.safe_load((ROOT / 'lefthook.yml').read_text()).items():
        for name, command in config.get('commands', {}).items():
            yield f'lefthook.yml:{hook}.{name}', command['run']
    script = ROOT / 'scripts/comment-policy-python'
    yield str(script.relative_to(ROOT)), script.read_text()


def hashed_file_problems(path):
    if not path.is_file():
        return [f'{path} does not exist']
    problems = []
    for line in logical_lines(path.read_text()):
        if not line.strip():
            continue
        tokens = shlex.split(line)
        if not REQUIREMENT.match(tokens[0]):
            problems.append(f'{path.name}: {tokens[0]!r} is not an exact name==version pin')
        hashes = tokens[1:]
        if not hashes or not all(HASH.match(token) for token in hashes):
            problems.append(f'{path.name}: {tokens[0]!r} needs only --hash=sha256 options, got {hashes}')
    return problems


def program(token):
    return token.rsplit('/', 1)[-1]


def uv_run_problems(args):
    problems = []
    options, target, directory = [], None, '.'
    remaining = iter(args)
    for arg in remaining:
        if arg == '--':
            target = next(remaining, None)
            break
        if arg == '-' or not arg.startswith('-'):
            target = arg
            break
        name, attached, value = arg.partition('=')
        options.append(name)
        if name in RUN_OPTIONS_WITH_VALUES:
            if not attached:
                value = next(remaining, '')
            if name == '--directory':
                directory = value
        elif name not in RUN_FLAGS:
            problems.append(f'uv run {arg} is not an option this check knows, so it cannot tell whether it hides the script uv runs')
    if '--no-project' not in options:
        problems.append('uv run needs --no-project before the command it runs, or it installs the nearest pyproject.toml project instead of a hashed requirements file')
    if any(re.fullmatch(r'--with([-=]\S*)?|-w\S*', arg) for arg in args):
        problems.append('uv run --with resolves packages without checking hashes; use uv pip install --require-hashes')
    if target == '-' or '://' in (target or ''):
        problems.append(f'uv run {target} runs a script whose PEP 723 dependencies this check cannot read')
    elif target and (SCRIPT_FLAGS & set(options) or target.lower().endswith(('.py', '.pyw'))):
        script = ROOT / directory / target
        if not script.is_file():
            problems.append(f'uv run names script {target!r}, which this check cannot read for PEP 723 dependencies')
        elif INLINE_METADATA.search(script.read_text(errors='replace')):
            problems.append(f'uv run resolves the PEP 723 dependencies in {target!r} without checking hashes')
    return problems


def wrapped_command(tokens, index, options_with_values, operands):
    index += 1
    while index < len(tokens) and tokens[index].startswith('-') and tokens[index] not in {'-', '--'}:
        token = tokens[index]
        if token.startswith('--'):
            takes_value = token in options_with_values
        else:
            letters = token[1:]
            first = next((position for position, letter in enumerate(letters) if f'-{letter}' in options_with_values), None)
            takes_value = first == len(letters) - 1
        index += 2 if takes_value else 1
    if tokens[index:index + 1] == ['--']:
        index += 1
    while index < len(tokens) and ASSIGNMENT.fullmatch(tokens[index]):
        index += 1
    return index + operands


def command_starts(tokens):
    starts, index = set(), 0
    while index < len(tokens):
        while index < len(tokens) and ASSIGNMENT.fullmatch(tokens[index]):
            index += 1
        if index < len(tokens) and tokens[index] not in SEPARATORS:
            starts.add(index)
            name = program(tokens[index])
            if name in KEYWORDS:
                index += 1
                continue
            if name in WRAPPERS:
                index = wrapped_command(tokens, index, *WRAPPERS[name])
                continue
        while index < len(tokens) and tokens[index] not in SEPARATORS and not tokens[index].endswith(';'):
            index += 1
        index += 1
    return starts


def install_problems(tokens):
    problems = []
    starts = command_starts(tokens)
    for start, token in enumerate(tokens):
        if program(token) in INSTALLERS:
            problems.append(f'{token} installs packages without a hashed requirements file')
        if token.startswith('UV_VENV_SEED='):
            problems.append('UV_VENV_SEED installs pip into the virtual environment without a hash')
        if token.startswith('UV_WORKING_DIR='):
            problems.append('UV_WORKING_DIR moves uv to another directory, so uv reads a script or requirements file this check did not read; use uv run --directory')
        if token == 'uv' or program(token) == 'uv' and start in starts:
            command = list(itertools.takewhile(lambda arg: arg not in SEPARATORS, tokens[start + 1:]))
            if command[:1] == ['run']:
                problems.extend(uv_run_problems(command[1:]))
            elif command[:1] == ['venv']:
                if '--seed' in command:
                    problems.append('uv venv --seed installs pip into the virtual environment without a hash')
            elif command[:2] != ['pip', 'install']:
                problems.append(f'uv {" ".join(command[:2])} is not one of uv venv, uv pip install or uv run --no-project')
        if PIP.fullmatch(program(token)) and tokens[start + 1:start + 2] == ['install']:
            if start == 0 or program(tokens[start - 1]) != 'uv':
                problems.append('use uv pip install, not pip install')
                continue
            args = tokens[start + 2:]
            for flag in ['--require-hashes', '--no-build']:
                if flag not in args:
                    problems.append(f'uv pip install needs {flag}')
            requirement_files = []
            remaining = iter(args)
            for arg in remaining:
                if arg in {'-r', '--requirement'}:
                    requirement_files.append(next(remaining, ''))
                elif arg == '--python':
                    next(remaining, None)
                elif arg in SEPARATORS:
                    break
                elif not arg.startswith('-'):
                    problems.append(f'uv pip install names {arg!r} outside a hashed requirements file')
            if not requirement_files:
                problems.append('uv pip install needs -r <hashed requirements file>')
            for name in requirement_files:
                problems.extend(hashed_file_problems(ROOT / name))
    return problems


def line_install_problems(line):
    if not re.search(r'\b(pip[0-9.]*|piptools|uvx|pipx|uv|UV_VENV_SEED|UV_WORKING_DIR)\b', line):
        return []
    return install_problems(shlex.split(line, comments=True))


def install_problems_in(sources):
    found = []
    for source, text in sources:
        for line in logical_lines(text):
            for problem in line_install_problems(line):
                found.append(f'{source}: {problem}: {line.strip()}')
    return found


def ci_requirement_files():
    files = set()
    for _, text in ci_sources():
        for line in logical_lines(text):
            if re.search(r'\buv\s+pip\s+install\b', line):
                tokens = shlex.split(line, comments=True)
                files.update(tokens[i + 1] for i, token in enumerate(tokens[:-1]) if token in {'-r', '--requirement'})
    return files


class CiPythonPackageTests(unittest.TestCase):
    maxDiff = None

    def test_every_ci_python_install_is_exactly_pinned_and_hashed(self):
        self.assertEqual(install_problems_in(ci_sources()), [])

    def test_lefthook_and_local_policy_runs_install_only_hashed_packages(self):
        self.assertEqual(install_problems_in(local_sources()), [])

    def test_lefthook_runs_the_comment_policy_through_the_hashed_script(self):
        hooks = dict(local_sources())
        self.assertTrue(hooks['lefthook.yml:pre-commit.no-comments'].startswith('scripts/comment-policy-python .comment-policy/check.py'))
        self.assertIn('--require-hashes --no-build -r .comment-policy/requirements.txt', hooks['scripts/comment-policy-python'])

    def test_ci_installs_each_suite_from_a_hashed_file(self):
        files = ci_requirement_files()
        self.assertGreaterEqual(len(files), MIN_HASHED_FILES, files)
        for name in sorted(files):
            with self.subTest(name=name):
                self.assertEqual(hashed_file_problems(ROOT / name), [])


class InstallRuleTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.hashed = self.directory / 'hashed.txt'
        self.hashed.write_text(f'pyyaml==6.0.3 \\\n    --hash=sha256:{"a" * 64}\n')

    def problems(self, command):
        return install_problems(shlex.split(command))

    def test_rejects_uv_run_with_in_any_spelling(self):
        for command in ["uv run --no-project --with 'pandas>=2.0' python", 'uv run --with pyyaml==6.0.3 python', 'uv run --with=pyyaml python', 'uv run -w pyyaml python', 'uv run --with-requirements r.txt python', 'uv run --with-editable . python']:
            with self.subTest(command=command):
                self.assertNotEqual(self.problems(command), [])

    def test_rejects_tool_installers_and_plain_pip(self):
        for command in ['uvx ruff check', 'uv tool run ruff', 'pipx run ruff', 'pip install pyyaml', 'python -m pip install -r r.txt --require-hashes']:
            with self.subTest(command=command):
                self.assertNotEqual(self.problems(command), [])

    def test_rejects_uv_pip_install_without_both_flags_or_with_named_packages(self):
        good = f'uv pip install --python "$RUNNER_TEMP/v" --require-hashes --no-build -r {self.hashed}'
        self.assertEqual(self.problems(good), [])
        for command in [good.replace(' --require-hashes', ''), good.replace(' --no-build', ''), good + ' pandas', 'uv pip install --require-hashes --no-build pandas==3.1.1', good.replace(str(self.hashed), str(self.directory / 'missing.txt'))]:
            with self.subTest(command=command):
                self.assertNotEqual(self.problems(command), [])

    def test_allows_uv_run_without_extra_packages(self):
        self.assertEqual(self.problems('uv run --no-project python -m unittest'), [])

    def test_rejects_versioned_pip_uv_sync_and_uv_run_that_reads_a_project(self):
        for line in ['pip3 install pyyaml', 'pip3.14 install --require-hashes -r r.txt', 'uv sync', 'uv sync --frozen', 'uv pip sync r.txt', 'cd apps/analysis && uv run python -m unittest', 'uv run --project apps/analysis python', 'uv --directory apps/analysis run python', 'uv run python -m unittest && uv run --no-project python -c 1']:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])

    def test_allows_the_commands_ci_runs(self):
        for line in ['uv venv "$RUNNER_TEMP/tests-ci"', f'uv pip install --python "$RUNNER_TEMP/v" --require-hashes --no-build -r {self.hashed}', 'uv run --no-project python -m unittest', 'bunx playwright install --with-deps chromium']:
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line), [])

    def test_rejects_path_qualified_installers_and_pip_tools(self):
        for line in ['/usr/bin/pip install pyyaml', '.venv/bin/pip3 install -r r.txt', '"$RUNNER_TEMP/v/bin/pip" install pyyaml', '/usr/local/bin/uv sync', '/opt/bin/uvx ruff check', '~/.local/bin/pipx run ruff', 'pip-sync r.txt', '/usr/bin/pip-sync r.txt', 'python -m piptools sync r.txt']:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])
        for line in [f'"$RUNNER_TEMP/bin/uv" pip install --python "$RUNNER_TEMP/v" --require-hashes --no-build -r {self.hashed}', '/usr/local/bin/uv venv "$RUNNER_TEMP/v"']:
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line), [])

    def test_rejects_a_seeded_uv_venv(self):
        for line in ['uv venv --seed "$RUNNER_TEMP/v"', 'uv venv "$RUNNER_TEMP/v" --seed', 'UV_VENV_SEED=1 uv venv "$RUNNER_TEMP/v"', 'export UV_VENV_SEED=true']:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])

    def test_no_project_counts_only_before_the_command_uv_runs(self):
        for line in ['uv run python -c 1 --no-project', 'uv run python -m unittest --no-project', 'uv run --python 3.14 python --no-project', 'uv run -- python --no-project']:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])
        for line in ['uv run --python 3.14 --no-project python -c 1', 'uv run -p 3.14 --no-project python -c 1', 'uv run --no-project -- python -c 1']:
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line), [])

    def test_rejects_uv_run_on_a_script_with_inline_dependencies(self):
        inline = '# /// script\n# dependencies = ["six"]\n# ///\nprint(1)\n'
        for name, text in {'inline.py': inline, 'inline': inline, 'inline.pyw': inline, 'plain.py': 'print(1)\n'}.items():
            (self.directory / name).write_text(text)
        rejected = ['inline.py', 'inline.pyw', '--script inline', '-s inline', '--gui-script inline', '-', 'https://example.invalid/script', 'missing.py']
        allowed = ['plain.py', 'python inline.py', 'python -m inline']
        for command, expected in [(c, False) for c in rejected] + [(c, True) for c in allowed]:
            words = [str(self.directory / word) if word.startswith(('inline', 'plain', 'missing')) else word for word in command.split()]
            line = shlex.join(['uv', 'run', '--no-project', *words])
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line) == [], expected, line_install_problems(line))

    def test_options_that_take_values_do_not_hide_the_script(self):
        inline = '# /// script\n# dependencies = ["six"]\n# ///\nprint(1)\n'
        sub = self.directory / 'sub'
        sub.mkdir()
        for path, text in {sub / 's.py': inline, sub / 'plain.py': 'print(1)\n', self.directory / 'UPPER.PY': inline, self.directory / 'mixed.Pyw': inline}.items():
            path.write_text(text)
        rejected = [
            ['--directory', str(sub), 's.py'],
            [f'--directory={sub}', 's.py'],
            ['--env-file', 'e', str(sub / 's.py')],
            ['--cache-dir', 'c', '-p', '3.14', str(sub / 's.py')],
            [str(self.directory / 'UPPER.PY')],
            [str(self.directory / 'mixed.Pyw')],
            ['--python-preference', 'only-system', str(sub / 's.py')],
            ['--native-tls', 'python', '-c', '1'],
        ]
        allowed = [
            ['--directory', str(sub), 'plain.py'],
            [f'--directory={sub}', 'plain.py'],
            ['--env-file', 'e', 'python', '-m', 'unittest'],
            ['--cache-dir', '$RUNNER_TEMP/uv', 'python', '-c', '1'],
            ['-q', '--frozen', '--env-file=e', 'python', '-c', '1'],
        ]
        for words, expected in [(w, False) for w in rejected] + [(w, True) for w in allowed]:
            line = shlex.join(['uv', 'run', '--no-project', *words])
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line) == [], expected, line_install_problems(line))

    def test_rejects_uv_working_dir_set_on_the_command_line(self):
        inline = '# /// script\n# dependencies = ["six"]\n# ///\nprint(1)\n'
        script = 'tests/ci/test_actions.py'
        (self.directory / script).parent.mkdir(parents=True)
        (self.directory / script).write_text(inline)
        self.assertEqual(line_install_problems(f'uv run --no-project {script}'), [])
        for line in [
            f'UV_WORKING_DIR={self.directory} uv run --no-project {script}',
            f'UV_CACHE_DIR=c UV_WORKING_DIR={self.directory} /usr/bin/uv run --no-project {script}',
            f'env UV_WORKING_DIR={self.directory} uv run --no-project {script}',
            f'export UV_WORKING_DIR={self.directory}; uv run --no-project {script}',
            'export UV_WORKING_DIR=sub',
            f'UV_WORKING_DIR={self.directory} uv pip install --python v --require-hashes --no-build -r scripts/ci/tests-ci-requirements.txt',
        ]:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])

    def test_a_path_ending_in_uv_is_uv_only_where_a_command_starts(self):
        for line in ['mkdir -p "$RUNNER_TEMP/uv"', 'directory="$RUNNER_TEMP/uv"', 'uv venv --cache-dir "$RUNNER_TEMP/uv" "$RUNNER_TEMP/v"', 'cp -r "$RUNNER_TEMP/uv" build && echo copied']:
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line), [])
        for line in ['"$RUNNER_TEMP/uv" sync', 'mkdir -p d && "$RUNNER_TEMP/bin/uv" sync', 'UV_CACHE_DIR=c /usr/bin/uv sync', 'timeout 60 uv sync', 'mise exec -- uv sync']:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])

    def test_a_path_ending_in_uv_counts_after_programs_and_keywords_that_run_a_command(self):
        for line in [
            'timeout 60 /usr/bin/uv sync',
            'timeout -s KILL 60 /usr/bin/uv sync',
            'timeout --signal KILL -k 5 60 /usr/bin/uv sync',
            'timeout --signal=KILL --preserve-status 60 /usr/bin/uv sync',
            'timeout -sKILL 60 /usr/bin/uv sync',
            '/usr/bin/timeout 60 /usr/bin/uv sync',
            'env /usr/bin/uv sync',
            'env -i HOME=/tmp /usr/bin/uv sync',
            'env -u HOME -C /tmp -- /usr/bin/uv sync',
            'nice /usr/bin/uv sync',
            'nice -n 5 /usr/bin/uv sync',
            'nice -5 /usr/bin/uv sync',
            'nohup /usr/bin/uv sync',
            'sudo /usr/bin/uv sync',
            'sudo -u runner "$RUNNER_TEMP/bin/uv" sync',
            'sudo -Eu runner /usr/bin/uv sync',
            'sudo --user=runner -- /usr/bin/uv sync',
            'sudo env A=1 nice -n 5 timeout -s KILL 60 /usr/bin/uv sync',
            'time /usr/bin/uv sync',
            'command /usr/bin/uv sync',
            'exec -a name /usr/bin/uv sync',
            'if /usr/bin/uv sync; then echo ok; fi',
            'if true; then /usr/bin/uv sync; fi',
            'if true; then echo; else /usr/bin/uv sync; fi',
            '! /usr/bin/uv sync',
            'while true; do /usr/bin/uv sync; done',
            '{ /usr/bin/uv sync; }',
        ]:
            with self.subTest(line=line):
                self.assertNotEqual(line_install_problems(line), [])
        for line in [
            'timeout 60 cp -r "$RUNNER_TEMP/uv" build',
            'timeout -s KILL 60 ls "$RUNNER_TEMP/uv"',
            'sudo -u runner mkdir -p "$RUNNER_TEMP/uv"',
            'env -C "$RUNNER_TEMP" ls "$RUNNER_TEMP/uv"',
            'if test -d "$RUNNER_TEMP/uv"; then echo ok; fi',
            'nice -n 5 tar -cf c.tar "$RUNNER_TEMP/uv"',
        ]:
            with self.subTest(line=line):
                self.assertEqual(line_install_problems(line), [])

    def test_hashed_file_rejects_ranges_missing_hashes_and_extra_options(self):
        digest = '--hash=sha256:' + 'a' * 64
        cases = {
            'range': f'pandas>=2.0 \\\n    {digest}\n',
            'unhashed': 'pandas==3.1.1\n',
            'index': f'pandas==3.1.1 \\\n    {digest} \\\n    --index-url=https://example.invalid\n',
            'md5': 'pandas==3.1.1 \\\n    --hash=md5:' + 'a' * 32 + '\n',
            'wildcard': f'pandas==3.* \\\n    {digest}\n',
        }
        good = self.directory / 'good.txt'
        good.write_text(f'pandas==3.1.1 \\\n    {digest} \\\n    --hash=sha256:{"b" * 64}\n')
        self.assertEqual(hashed_file_problems(good), [])
        for name, text in cases.items():
            with self.subTest(name=name):
                path = self.directory / (name + '.txt')
                path.write_text(text)
                self.assertNotEqual(hashed_file_problems(path), [])


if __name__ == '__main__':
    unittest.main()
