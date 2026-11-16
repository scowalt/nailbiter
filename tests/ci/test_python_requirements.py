from pathlib import Path
import re
import shlex
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
REQUIREMENT = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._-]*==[0-9][A-Za-z0-9.+!-]*$')
HASH = re.compile(r'^--hash=sha256:[0-9a-f]{64}$')
INSTALLERS = {'uvx', 'pipx'}
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
        if token in INSTALLERS or tokens[start:start + 2] == ['uv', 'tool']:
            problems.append(f'{token} installs packages without a hashed requirements file')
        if tokens[start:start + 2] == ['uv', 'run'] and any(re.fullmatch(r'--with([-=]\S*)?|-w\S*', arg) for arg in tokens[start + 2:]):
            problems.append('uv run --with resolves packages without checking hashes; use uv pip install --require-hashes')
        if token == 'pip' and tokens[start + 1:start + 2] == ['install']:
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
                elif arg in {'&&', '||', ';', '|'}:
                    break
                elif not arg.startswith('-'):
                    problems.append(f'uv pip install names {arg!r} outside a hashed requirements file')
            if not requirement_files:
                problems.append('uv pip install needs -r <hashed requirements file>')
            for name in requirement_files:
                problems.extend(hashed_file_problems(ROOT / name))
    return problems


def ci_install_problems():
    found = []
    for source, text in ci_sources():
        for line in logical_lines(text):
            if not re.search(r'\b(pip|uvx|pipx|uv\s+run|uv\s+tool)\b', line):
                continue
            for problem in install_problems(shlex.split(line, comments=True)):
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
