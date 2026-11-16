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
INSTALLERS = {'uvx', 'pipx'}
PIP = re.compile(r'pip[0-9.]*')
SEPARATORS = {'&&', '||', ';', '|'}
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


def install_problems(tokens):
    problems = []
    for start, token in enumerate(tokens):
        if token in INSTALLERS:
            problems.append(f'{token} installs packages without a hashed requirements file')
        if token == 'uv':
            command = list(itertools.takewhile(lambda arg: arg not in SEPARATORS, tokens[start + 1:]))
            if command[:1] == ['run']:
                if '--no-project' not in command:
                    problems.append('uv run without --no-project installs the nearest pyproject.toml project instead of a hashed requirements file')
                if any(re.fullmatch(r'--with([-=]\S*)?|-w\S*', arg) for arg in command):
                    problems.append('uv run --with resolves packages without checking hashes; use uv pip install --require-hashes')
            elif command[:1] != ['venv'] and command[:2] != ['pip', 'install']:
                problems.append(f'uv {" ".join(command[:2])} is not one of uv venv, uv pip install or uv run --no-project')
        if PIP.fullmatch(token) and tokens[start + 1:start + 2] == ['install']:
            if tokens[start - 1:start] != ['uv']:
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
    if not re.search(r'\b(pip[0-9.]*|uvx|pipx|uv)\b', line):
        return []
    return install_problems(shlex.split(line, comments=True))


def ci_install_problems():
    found = []
    for source, text in ci_sources():
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
        self.assertEqual(ci_install_problems(), [])

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
