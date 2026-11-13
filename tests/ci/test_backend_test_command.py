import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = 'scripts/test-backend'
FSHARP = 'apps/backend.fsharp.tests/backend.fsharp.tests.fsproj'
CSHARP = 'apps/backend.tests/backend.tests.csproj'
EXIT = {'passed': 0, 'tests failed': 1, 'invalid selection': 2, 'build failed': 3, 'no tests matched': 4, 'toolchain': 5}

FAKE_DOTNET = r'''
import json, os, re, sys, time
from pathlib import Path
from xml.sax.saxutils import escape, quoteattr

scenario = json.loads(Path(os.environ['FAKE_SCENARIO']).read_text())
argv = sys.argv[1:]

def journal(**entry):
    entry.update(tag=os.environ.get('FAKE_TAG', ''), pid=os.getpid(), argv=argv, mise=os.environ.get('FAKE_FROM_MISE'))
    with open(os.environ['FAKE_JOURNAL'], 'a') as handle:
        handle.write(json.dumps(entry) + '\n')

def option(name):
    return argv[argv.index(name) + 1] if name in argv else None

if argv[0] == 'build':
    projects = re.findall(r'Path="([^"]+)"', Path(argv[1]).read_text())
    journal(cmd='build', phase='start', projects=projects)
    print('  Determining projects to restore...')
    time.sleep(scenario.get('build_sleep', 0))
    if scenario.get('build_error'):
        print(scenario['build_error'])
        print('Build FAILED.')
        journal(cmd='build', phase='end')
        sys.exit(1)
    print('Build succeeded.')
    journal(cmd='build', phase='end')
    sys.exit(0)

project = Path(argv[1]).stem
journal(cmd='test', phase='start', project=project)
tests = scenario['suites'][project]
selection = option('--filter')
if selection and '(' in selection:
    print(f'[xUnit.net 00:00:00.39] {project}: Exception filtering tests: Incorrect format for TestCaseFilter Error: Invalid Condition. Specify the correct format and try again.')
    chosen = {}
elif selection:
    needles = [part.split('~', 1)[1] for part in selection.split('|')]
    chosen = {name: result for name, result in tests.items() if any(needle in name for needle in needles)}
else:
    chosen = tests
logger = next(argv[i + 1] for i, arg in enumerate(argv) if arg == '--logger' and argv[i + 1].startswith('trx'))
trx = Path(option('--results-directory')) / re.search(r'LogFileName=([^;]+)', logger).group(1)
trx.parent.mkdir(parents=True, exist_ok=True)
rows = []
for name, result in chosen.items():
    if result == 'pass':
        rows.append(f'<UnitTestResult testName={quoteattr(name)} outcome="Passed" />')
    else:
        print(f'  Failed {name} [1 ms]')
        rows.append(f'<UnitTestResult testName={quoteattr(name)} outcome="Failed"><Output><ErrorInfo><Message>{escape(result["message"])}</Message><StackTrace>{escape(result["stack"])}</StackTrace></ErrorInfo></Output></UnitTestResult>')
failed = sum(result != 'pass' for result in chosen.values())
trx.write_text(f'<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{"".join(rows)}</Results><ResultSummary outcome="Completed"><Counters total="{len(chosen)}" executed="{len(chosen)}" passed="{len(chosen) - failed}" failed="{failed}" /></ResultSummary></TestRun>')
if not chosen and selection:
    print(f'No test matches the given testcase filter `{selection}` in {project}.dll')
print(f'Results File: {trx}')
journal(cmd='test', phase='end', project=project)
sys.exit(1 if failed else 0)
'''

FAKE_MISE = r'''
import json, os, sys
if sys.argv[1:3] != ['env', '--json'] or os.environ.get('MISE_AUTO_INSTALL') != '0':
    sys.exit(f'unexpected mise call {sys.argv[1:]}')
print(json.dumps({'FAKE_FROM_MISE': '1'}))
'''

PASSING = {
    'backend.fsharp.tests': {'FormatTests.formats a score': 'pass', 'MoneyPuckPolicyTests.rejects a stale file': 'pass'},
    'backend.tests': {'NflPlayoffLeverageTests.Leverage_is_bounded': 'pass', 'MoneyPuckDailyRefreshTests.Refresh_runs_once': 'pass'},
}
STACK = '\n'.join(f'   at Frame{n}() in /src/Frame{n}.cs:line {n}' for n in range(1, 31))
FAILING = {
    'backend.fsharp.tests': PASSING['backend.fsharp.tests'],
    'backend.tests': {
        **PASSING['backend.tests'],
        'NflPlayoffLeverageTests.Leverage_is_symmetric': {'message': 'Assert.Equal() Failure: Values differ\nExpected: 0.25\nActual:   0.75', 'stack': STACK},
    },
}


def write_executable(path, body):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f'#!{sys.executable}\n{body}')
    path.chmod(0o755)


def make_worktree(parent, name):
    worktree = parent / name
    (worktree / 'scripts').mkdir(parents=True)
    shutil.copy2(ROOT / SCRIPT, worktree / SCRIPT)
    return worktree


class Harness:
    def __init__(self, test, scenario):
        self.temp = Path(tempfile.mkdtemp(prefix='nailbiter-test-backend-'))
        test.addCleanup(shutil.rmtree, self.temp, ignore_errors=True)
        self.bin = self.temp / 'bin'
        write_executable(self.bin / 'dotnet', FAKE_DOTNET)
        write_executable(self.bin / 'mise', FAKE_MISE)
        self.journal_path = self.temp / 'journal.jsonl'
        self.journal_path.touch()
        self.scenario = self.temp / 'scenario.json'
        self.scenario.write_text(json.dumps(scenario))
        self.worktree = make_worktree(self.temp, 'worktree')

    def env(self, tag='', path=None):
        return {
            'HOME': str(self.temp),
            'PATH': path if path is not None else f'{self.bin}:/usr/bin:/bin',
            'FAKE_SCENARIO': str(self.scenario),
            'FAKE_JOURNAL': str(self.journal_path),
            'FAKE_TAG': tag,
        }

    def start(self, *args, tag='', worktree=None, path=None):
        return subprocess.Popen([sys.executable, str((worktree or self.worktree) / SCRIPT), *args], cwd=worktree or self.worktree, env=self.env(tag, path), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def run(self, *args, **kwargs):
        process = self.start(*args, **kwargs)
        output, _ = process.communicate(timeout=60)
        return process.returncode, output

    def journal(self):
        return [json.loads(line) for line in self.journal_path.read_text().splitlines()]

    def wait_for(self, predicate):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if any(predicate(entry) for entry in self.journal()):
                return
            time.sleep(0.05)
        raise AssertionError(f'journal never matched: {self.journal()}')


def calls(journal, phase='start'):
    return [entry for entry in journal if entry['phase'] == phase]


def option(argv, name):
    return argv[argv.index(name) + 1]


class SelectionAndBuildTests(unittest.TestCase):
    def test_one_build_of_every_selected_suite_then_tests_without_building(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run()
        self.assertEqual(code, EXIT['passed'], output)
        started = calls(harness.journal())
        self.assertEqual([entry['cmd'] for entry in started], ['build', 'test', 'test'], output)
        build = started[0]
        self.assertEqual(build['projects'], [str(harness.worktree / FSHARP), str(harness.worktree / CSHARP)])
        self.assertEqual(option(build['argv'], '--configuration'), 'Release')
        self.assertIn('-p:ShouldUnsetParentConfigurationAndPlatform=false', build['argv'], 'projects the suites reference build in the selected configuration, not Debug')
        self.assertEqual([entry['project'] for entry in started[1:]], ['backend.fsharp.tests', 'backend.tests'])
        for entry in started[1:]:
            self.assertIn('--no-build', entry['argv'])
            self.assertEqual(option(entry['argv'], '--configuration'), 'Release')
        self.assertTrue(all(entry['mise'] == '1' for entry in started), 'dotnet runs with the environment mise reports')
        self.assertRegex(output, r'fsharp\s+passed\s+2 passed, 0 failed')
        self.assertRegex(output, r'csharp\s+passed\s+2 passed, 0 failed')

    def test_a_selected_suite_builds_and_tests_only_that_suite(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run('csharp', '--configuration', 'Debug')
        self.assertEqual(code, EXIT['passed'], output)
        started = calls(harness.journal())
        self.assertEqual(started[0]['projects'], [str(harness.worktree / CSHARP)])
        self.assertEqual([entry.get('project') for entry in started[1:]], ['backend.tests'])
        self.assertTrue(all(option(entry['argv'], '--configuration') == 'Debug' for entry in started))

    def test_filter_reaches_every_selected_suite(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run('--filter', 'FullyQualifiedName~MoneyPuck')
        self.assertEqual(code, EXIT['passed'], output)
        tests = [entry for entry in calls(harness.journal()) if entry['cmd'] == 'test']
        self.assertEqual(len(tests), 2)
        self.assertTrue(all(option(entry['argv'], '--filter') == 'FullyQualifiedName~MoneyPuck' for entry in tests))
        self.assertRegex(output, r'fsharp\s+passed\s+1 passed')
        self.assertRegex(output, r'csharp\s+passed\s+1 passed')

    def test_unmatched_filter_fails_and_names_the_suite(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run('--filter', 'FullyQualifiedName~NflPlayoffLeverage')
        self.assertEqual(code, EXIT['no tests matched'], output)
        self.assertRegex(output, r'fsharp\s+no tests matched')
        self.assertRegex(output, r'csharp\s+passed\s+1 passed')

    def test_invalid_filter_is_an_invalid_selection(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run('fsharp', '--filter', 'FullyQualifiedName~(')
        self.assertEqual(code, EXIT['invalid selection'], output)
        self.assertIn('Incorrect format for TestCaseFilter', output)

    def test_unknown_suite_is_an_invalid_selection_and_builds_nothing(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run('backend')
        self.assertEqual(code, EXIT['invalid selection'], output)
        self.assertEqual(harness.journal(), [])

    def test_build_failure_shows_the_error_and_runs_no_tests(self):
        error = '/src/apps/backend/Program.cs(10,5): error CS1002: ; expected [/src/apps/backend/backend.csproj]'
        harness = Harness(self, {'suites': PASSING, 'build_error': error})
        code, output = harness.run()
        self.assertEqual(code, EXIT['build failed'], output)
        self.assertIn('error CS1002: ; expected', output)
        self.assertEqual([entry['cmd'] for entry in calls(harness.journal())], ['build'])

    def test_failing_test_shows_its_assertion_and_stack_head_on_the_first_run(self):
        harness = Harness(self, {'suites': FAILING})
        code, output = harness.run()
        self.assertEqual(code, EXIT['tests failed'], output)
        self.assertIn('NflPlayoffLeverageTests.Leverage_is_symmetric', output)
        self.assertIn('Assert.Equal() Failure: Values differ', output)
        self.assertIn('Expected: 0.25', output)
        self.assertIn('Actual:   0.75', output)
        self.assertIn('at Frame1() in /src/Frame1.cs:line 1', output)
        self.assertNotIn('Frame30', output)
        self.assertRegex(output, r'fsharp\s+passed\s+2 passed, 0 failed')
        self.assertRegex(output, r'csharp\s+FAILED\s+2 passed, 1 failed')

    def test_a_missing_mise_is_a_toolchain_failure(self):
        harness = Harness(self, {'suites': PASSING})
        bare = harness.temp / 'bare'
        write_executable(bare / 'dotnet', FAKE_DOTNET)
        code, output = harness.run(path=str(bare))
        self.assertEqual(code, EXIT['toolchain'], output)
        self.assertIn('mise', output)
        self.assertEqual(harness.journal(), [])

    def test_every_outcome_has_its_own_exit_code(self):
        self.assertEqual(len(set(EXIT.values())), len(EXIT))
        self.assertNotIn(0, [code for outcome, code in EXIT.items() if outcome != 'passed'])

    def test_logs_stay_in_the_ignored_worktree_obj_directory_without_environment_dumps(self):
        harness = Harness(self, {'suites': FAILING})
        code, output = harness.run()
        self.assertEqual(code, EXIT['tests failed'], output)
        logs = harness.worktree / 'obj/test-backend'
        retained = [path for path in logs.rglob('*') if path.is_file()]
        self.assertTrue(any(path.suffix == '.log' for path in retained), output)
        self.assertIn(str(logs.relative_to(harness.worktree)), output)
        ignored = subprocess.run(['git', 'check-ignore', '-q', 'obj/test-backend/run/build.log'], cwd=ROOT)
        self.assertEqual(ignored.returncode, 0, 'obj/test-backend is gitignored')
        for entry in harness.journal():
            self.assertFalse({'-bl', '/bl', '-binaryLogger'} & set(entry['argv']), 'binary logs record the environment')
            self.assertFalse([arg for arg in entry['argv'] if re.search(r'(^|[:=])diag', arg)], 'diagnostic verbosity prints the environment')


class ConcurrencyTests(unittest.TestCase):
    def test_a_second_invocation_in_one_worktree_waits_for_the_first(self):
        harness = Harness(self, {'suites': PASSING, 'build_sleep': 1.5})
        first = harness.start('fsharp', tag='first')
        harness.wait_for(lambda entry: entry['tag'] == 'first' and entry['cmd'] == 'build')
        second = harness.start('csharp', tag='second')
        second_output, _ = second.communicate(timeout=60)
        first_output, _ = first.communicate(timeout=60)
        self.assertEqual(first.returncode, 0, first_output)
        self.assertEqual(second.returncode, 0, second_output)
        self.assertIn('waiting', second_output)
        tags = [entry['tag'] for entry in harness.journal()]
        self.assertEqual(tags, ['first'] * 4 + ['second'] * 4, harness.journal())

    def test_another_worktree_runs_alongside(self):
        harness = Harness(self, {'suites': PASSING, 'build_sleep': 1.5})
        other = make_worktree(harness.temp, 'other')
        first = harness.start('fsharp', tag='first')
        harness.wait_for(lambda entry: entry['tag'] == 'first' and entry['cmd'] == 'build')
        second = harness.start('fsharp', tag='other', worktree=other)
        second_output, _ = second.communicate(timeout=60)
        first_output, _ = first.communicate(timeout=60)
        self.assertEqual((first.returncode, second.returncode), (0, 0), first_output + second_output)
        self.assertNotIn('waiting', second_output)
        builds = [(entry['tag'], entry['phase']) for entry in harness.journal() if entry['cmd'] == 'build']
        self.assertEqual(builds, [('first', 'start'), ('other', 'start'), ('first', 'end'), ('other', 'end')])

    def test_stopping_an_invocation_stops_its_build_and_frees_the_worktree(self):
        harness = Harness(self, {'suites': PASSING, 'build_sleep': 30})
        first = harness.start('fsharp', tag='first')
        harness.wait_for(lambda entry: entry['tag'] == 'first' and entry['cmd'] == 'build')
        build_pid = calls(harness.journal())[0]['pid']
        first.send_signal(signal.SIGTERM)
        first.communicate(timeout=30)
        self.assertNotEqual(first.returncode, 0)
        deadline = time.monotonic() + 15
        while Path(f'/proc/{build_pid}').exists() and time.monotonic() < deadline:
            time.sleep(0.1)
        self.assertFalse(Path(f'/proc/{build_pid}').exists(), 'the build stops with its invocation')
        harness.scenario.write_text(json.dumps({'suites': PASSING}))
        code, output = harness.run('fsharp', tag='next')
        self.assertEqual(code, 0, output)
        self.assertNotIn('waiting', output)


if __name__ == '__main__':
    unittest.main()
