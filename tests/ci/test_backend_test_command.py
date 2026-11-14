import contextlib
from datetime import datetime
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
from datetime import datetime
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

culture = next((os.environ[name] for name in ('LC_ALL', 'LC_MESSAGES', 'LANG') if os.environ.get(name)), 'C').split('.')[0]
language = (os.environ.get('DOTNET_CLI_UI_LANGUAGE') or {'1031': 'de-DE'}.get(os.environ.get('VSLANG', '')) or culture)[:2]

if argv[0] == 'build':
    projects = re.findall(r'Path="([^"]+)"', Path(argv[1]).read_text())
    journal(cmd='build', phase='start', projects=projects)
    started = datetime.fromisoformat(scenario.get('build_started') or datetime.now().isoformat())
    layout = {'de_DE': '%d.%m.%Y %H:%M:%S', 'en_GB': '%d/%m/%Y %H:%M:%S', 'en_US': '%m/%d/%Y %-I:%M:%S %p'}.get(culture, '%m/%d/%Y %H:%M:%S')
    print(f'Der Buildvorgang wurde am {started:{layout}} gestartet.' if language == 'de' else f'Build started {started:{layout}}.')
    print('  Determining projects to restore...')
    time.sleep(scenario.get('build_sleep', 0))
    if scenario.get('build_error'):
        print(f'     3>{scenario["build_error"]}')
        print('Build FAILED.')
        print(f'       {scenario["build_error"]}')
        journal(cmd='build', phase='end')
        sys.exit(1)
    print('Build succeeded.')
    journal(cmd='build', phase='end')
    sys.exit(0)

project = Path(argv[1]).stem
journal(cmd='test', phase='start', project=project)
if scenario.get('test_sleep'):
    import subprocess
    host = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)', os.environ['FAKE_JOURNAL']])
    journal(cmd='testhost', phase='spawn', host=host.pid)
    time.sleep(scenario['test_sleep'])
tests = scenario['suites'][project]
selection = option('--filter')
if selection and '(' in selection:
    if language == 'de':
        print(f'[xUnit.net 00:00:00.39] {project}: Exception filtering tests: Ungültiges Format für TestCaseFilter Fehler: Ungültige Bedingung. Geben Sie das richtige Format an, und versuchen Sie es erneut.')
    else:
        print(f'[xUnit.net 00:00:00.39] {project}: Exception filtering tests: Incorrect format for TestCaseFilter Error: Invalid Condition. Specify the correct format and try again.')
    chosen = {}
elif selection:
    needles = [part.split('~', 1)[1] for part in selection.split('|')]
    chosen = {name: result for name, result in tests.items() if any(needle in name for needle in needles)}
else:
    chosen = tests
logger = next(argv[i + 1] for i, arg in enumerate(argv) if arg == '--logger' and argv[i + 1].startswith('trx'))
trx = Path(option('--results-directory')) / re.search(r'LogFileName=([^;]+)', logger).group(1)
rows = []
counts = {'Passed': 0, 'Failed': 0, 'Error': 0, 'NotExecuted': 0}
for name, result in chosen.items():
    outcome = {'pass': 'Passed', 'skip': 'NotExecuted'}[result] if isinstance(result, str) else result.get('outcome', 'Failed')
    counts[outcome] += 1
    if outcome in ('Passed', 'NotExecuted'):
        rows.append(f'<UnitTestResult testName={quoteattr(name)} outcome="{outcome}" />')
    else:
        print(f'  Failed {name} [1 ms]')
        rows.append(f'<UnitTestResult testName={quoteattr(name)} outcome="{outcome}"><Output><ErrorInfo><Message>{escape(result["message"])}</Message><StackTrace>{escape(result["stack"])}</StackTrace></ErrorInfo></Output></UnitTestResult>')
run = scenario.get('runs', {}).get(project, {})
totals = {'total': len(chosen), 'executed': len(chosen) - counts['NotExecuted'], 'passed': counts['Passed'], 'failed': counts['Failed'], 'error': counts['Error'], 'notExecuted': 0}
totals.update(run.get('counters', {}))
counters = '<Counters ' + ' '.join(f'{name}="{value}"' for name, value in totals.items()) + ' />'
if run.get('trx') != 'missing':
    trx.write_text(f'<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{"".join(rows)}</Results><ResultSummary outcome="Completed">{"" if run.get("trx") == "no-counters" else counters}</ResultSummary></TestRun>')
if not chosen and selection:
    print(f'No test matches the given testcase filter `{selection}` in {project}.dll')
print(f'Results File: {trx}')
if run.get('crash'):
    print(run['crash'])
journal(cmd='test', phase='end', project=project)
sys.exit(run.get('exit', 1 if counts['Failed'] + counts['Error'] else 0))
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


def locale_caller(locale):
    return {'LANG': locale, 'LC_ALL': locale}


GERMAN_CALLERS = (locale_caller('de_DE.UTF-8'), {'DOTNET_CLI_UI_LANGUAGE': 'de-DE'}, {'VSLANG': '1031'})


def write_executable(path, body):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f'#!{sys.executable}\n{body}')
    path.chmod(0o755)


def make_worktree(parent, name):
    worktree = parent / name
    (worktree / 'scripts').mkdir(parents=True)
    shutil.copy2(ROOT / SCRIPT, worktree / SCRIPT)
    return worktree


def sandbox(test, prefix):
    temp = Path(tempfile.mkdtemp(prefix=prefix))
    test.addCleanup(shutil.rmtree, temp, ignore_errors=True)
    test.addCleanup(stop_processes_under, temp)
    return temp


def process_table():
    for stat in Path('/proc').glob('[0-9]*/stat'):
        with contextlib.suppress(OSError):
            state, _, group = stat.read_text().rsplit(')', 1)[1].split()[:3]
            yield int(stat.parent.name), state, int(group)


def processes_under(temp):
    marker = str(temp)
    for pid, _, _ in process_table():
        with contextlib.suppress(OSError):
            if pid != os.getpid() and (marker.encode() in Path(f'/proc/{pid}/cmdline').read_bytes() or os.readlink(f'/proc/{pid}/cwd').startswith(marker)):
                yield pid


def stop_processes_under(temp):
    deadline = time.monotonic() + 10
    while (pids := list(processes_under(temp))) and time.monotonic() < deadline:
        for pid in pids:
            with contextlib.suppress(ProcessLookupError):
                os.kill(pid, signal.SIGKILL)
        time.sleep(0.1)


def trx_finish(trx):
    finish = re.search(r'<Times [^>]*finish="([^"]+)"', trx.read_text()).group(1)
    return datetime.fromisoformat(finish).astimezone().replace(tzinfo=None)


def msbuild_start(log):
    started = re.match(r'Build started (.+)\.', log.read_text()).group(1)
    return datetime.strptime(started, '%m/%d/%Y %H:%M:%S')


def assert_built_after(test, waiter_log, holder_trx):
    test.assertGreaterEqual(msbuild_start(waiter_log), trx_finish(holder_trx).replace(microsecond=0), 'the waiting run built before the holder had finished its tests')


class SandboxedTestCase(unittest.TestCase):
    def run(self, result=None):
        try:
            return super().run(result)
        finally:
            self.doCleanups()


class Harness:
    def __init__(self, test, scenario):
        self.temp = sandbox(test, 'nailbiter-test-backend-')
        self.bin = self.temp / 'bin'
        write_executable(self.bin / 'dotnet', FAKE_DOTNET)
        write_executable(self.bin / 'mise', FAKE_MISE)
        self.journal_path = self.temp / 'journal.jsonl'
        self.journal_path.touch()
        self.scenario = self.temp / 'scenario.json'
        self.scenario.write_text(json.dumps(scenario))
        self.worktree = make_worktree(self.temp, 'worktree')

    def env(self, tag='', path=None, caller=None):
        return {
            'HOME': str(self.temp),
            'PATH': path if path is not None else f'{self.bin}:/usr/bin:/bin',
            'FAKE_SCENARIO': str(self.scenario),
            'FAKE_JOURNAL': str(self.journal_path),
            'FAKE_TAG': tag,
            **(caller or {}),
        }

    def start(self, *args, tag='', worktree=None, path=None, caller=None):
        return subprocess.Popen([sys.executable, str((worktree or self.worktree) / SCRIPT), *args], cwd=worktree or self.worktree, env=self.env(tag, path, caller), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

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


HOLDER = '''
import fcntl, os, sys, time
handle = open(sys.argv[1], 'a+')
fcntl.flock(handle, fcntl.LOCK_EX)
print('locked', flush=True)
time.sleep(0.6)
handle.seek(0)
handle.truncate()
handle.write(f'scripts/test-backend all --configuration Release (pid {os.getpid()}, started 09:30:00)\\n')
handle.flush()
time.sleep(0.6)
'''


def group_members(group):
    return [pid for pid, state, process_group in process_table() if process_group == group and state != 'Z']


def kill_group(group):
    with contextlib.suppress(ProcessLookupError):
        os.killpg(group, signal.SIGKILL)


def calls(journal, phase='start'):
    return [entry for entry in journal if entry['phase'] == phase]


def option(argv, name):
    return argv[argv.index(name) + 1]


class SelectionAndBuildTests(SandboxedTestCase):
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

    def test_an_invalid_filter_is_an_invalid_selection_in_any_caller_language(self):
        for caller in GERMAN_CALLERS:
            with self.subTest(**caller):
                harness = Harness(self, {'suites': PASSING})
                code, output = harness.run('csharp', '--filter', 'FullyQualifiedName~(', caller=caller)
                self.assertEqual(code, EXIT['invalid selection'], output)
                self.assertIn('Incorrect format for TestCaseFilter', output)

    def test_the_build_order_check_catches_an_early_build_in_any_caller_language(self):
        holder_finished = datetime(2026, 5, 12, 13, 49, 24).astimezone()
        for caller in (*GERMAN_CALLERS, locale_caller('en_GB.UTF-8'), locale_caller('en_US.UTF-8')):
            with self.subTest(**caller):
                harness = Harness(self, {'suites': PASSING, 'build_started': '2026-05-12T13:49:19'})
                code, output = harness.run('csharp', caller=caller)
                self.assertEqual(code, EXIT['passed'], output)
                holder = harness.temp / 'holder.trx'
                holder.write_text(f'<Times finish="{holder_finished.isoformat()}" />')
                waiter = next(harness.worktree.glob('obj/test-backend/runs/*/build.log'))
                with self.assertRaisesRegex(AssertionError, 'built before the holder'):
                    assert_built_after(self, waiter, holder)

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
        self.assertEqual(output.count('error CS1002: ; expected'), 1, output)
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

    def test_runs_that_end_without_a_clean_result_fail(self):
        crash = 'The active test run was aborted. Reason: Test host process crashed'
        cases = {
            'nonzero exit with every test passed': {'backend.tests': {'exit': 1, 'crash': crash}},
            'no results file': {'backend.tests': {'trx': 'missing', 'crash': crash}},
            'results file without counters': {'backend.tests': {'trx': 'no-counters', 'exit': 1, 'crash': crash}},
        }
        for case, runs in cases.items():
            with self.subTest(case):
                harness = Harness(self, {'suites': PASSING, 'runs': runs})
                code, output = harness.run('csharp')
                self.assertEqual(code, EXIT['tests failed'], output)
                self.assertRegex(output, r'csharp\s+FAILED')
                self.assertIn(crash, output)
                self.assertNotIn('Traceback', output)

    def test_errored_tests_show_their_message_and_skips_are_not_errors(self):
        suites = {**PASSING, 'backend.tests': {
            'HostTests.Boots': 'pass',
            'HostTests.Waits': 'skip',
            'HostTests.Crashes': {'outcome': 'Error', 'message': 'System.TimeoutException: host did not start', 'stack': STACK},
        }}
        harness = Harness(self, {'suites': suites})
        code, output = harness.run('csharp')
        self.assertEqual(code, EXIT['tests failed'], output)
        self.assertIn('System.TimeoutException: host did not start', output)
        self.assertRegex(output, r'csharp\s+FAILED\s+1 passed, 1 failed, 1 skipped')

    def test_printed_counts_come_from_the_listed_results(self):
        harness = Harness(self, {'suites': FAILING, 'runs': {'backend.tests': {'counters': {'passed': 7, 'failed': 3, 'error': 2}}}})
        code, output = harness.run('csharp')
        self.assertEqual(code, EXIT['tests failed'], output)
        self.assertRegex(output, r'csharp\s+FAILED\s+2 passed, 1 failed, 0 skipped')

    def test_trx_counters_that_disagree_with_the_listed_failures_fail_the_run(self):
        harness = Harness(self, {'suites': PASSING, 'runs': {'backend.tests': {'counters': {'failed': 1, 'aborted': 1}, 'exit': 0}}})
        code, output = harness.run('csharp')
        self.assertEqual(code, EXIT['tests failed'], output)
        self.assertRegex(output, r'csharp\s+FAILED\s+2 passed, 0 failed, 0 skipped')
        self.assertIn('TRX counters report 2 failed tests, but it lists 0', output)

    def test_a_selection_whose_tests_are_all_skipped_ran_no_test(self):
        harness = Harness(self, {'suites': {**PASSING, 'backend.tests': {'HostTests.Waits': 'skip'}}})
        code, output = harness.run('csharp')
        self.assertEqual(code, EXIT['no tests matched'], output)

    def test_an_invalid_filter_stops_before_the_next_suite(self):
        harness = Harness(self, {'suites': PASSING})
        code, output = harness.run('--filter', 'FullyQualifiedName~(')
        self.assertEqual(code, EXIT['invalid selection'], output)
        self.assertEqual([entry.get('project') for entry in calls(harness.journal())], [None, 'backend.fsharp.tests'])

    def test_mise_output_that_is_not_json_is_a_toolchain_failure(self):
        harness = Harness(self, {'suites': PASSING})
        write_executable(harness.bin / 'mise', "print('mise WARN  config not trusted')\n")
        code, output = harness.run()
        self.assertEqual(code, EXIT['toolchain'], output)
        self.assertNotIn('Traceback', output)

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
            self.assertFalse([arg for arg in entry['argv'] if re.search(r'^-(v|verbosity):(d|detailed|diag|diagnostic)$', arg)], 'detailed and diagnostic verbosity print the environment')


class ConcurrencyTests(SandboxedTestCase):
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

    def test_the_wait_message_names_a_holder_that_has_not_written_its_name_yet(self):
        harness = Harness(self, {'suites': PASSING})
        lock = harness.worktree / 'obj/test-backend/lock'
        lock.parent.mkdir(parents=True)
        lock.write_text('scripts/test-backend csharp --configuration Release (pid 999999999, started 09:00:00)\n')
        holder = subprocess.Popen([sys.executable, '-c', HOLDER, str(lock)], stdout=subprocess.PIPE, text=True)
        self.addCleanup(holder.stdout.close)
        self.addCleanup(holder.kill)
        self.assertEqual(holder.stdout.readline().strip(), 'locked')
        code, output = harness.run('fsharp')
        holder.wait(timeout=30)
        self.assertEqual(code, EXIT['passed'], output)
        self.assertIn(f'waiting for scripts/test-backend all --configuration Release (pid {holder.pid}, started 09:30:00) to finish', output)

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
        first_output, _ = first.communicate(timeout=30)
        self.assertEqual(first.returncode, 128 + signal.SIGTERM, first_output)
        self.assertNotIn('Traceback', first_output)
        deadline = time.monotonic() + 15
        while Path(f'/proc/{build_pid}').exists() and time.monotonic() < deadline:
            time.sleep(0.1)
        self.assertFalse(Path(f'/proc/{build_pid}').exists(), 'the build stops with its invocation')
        harness.scenario.write_text(json.dumps({'suites': PASSING}))
        code, output = harness.run('fsharp', tag='next')
        self.assertEqual(code, 0, output)
        self.assertNotIn('waiting', output)

    def test_stopping_an_invocation_stops_every_process_of_its_test_run(self):
        for stop, code in ((signal.SIGTERM, 128 + signal.SIGTERM), (signal.SIGINT, 130)):
            with self.subTest(stop.name):
                harness = Harness(self, {'suites': PASSING, 'test_sleep': 60})
                first = harness.start('fsharp')
                harness.wait_for(lambda entry: entry['cmd'] == 'testhost')
                group = next(entry['pid'] for entry in calls(harness.journal()) if entry['cmd'] == 'test')
                host = next(entry['host'] for entry in harness.journal() if entry['cmd'] == 'testhost')
                self.addCleanup(kill_group, group)
                self.assertIn(host, group_members(group))
                first.send_signal(stop)
                output, _ = first.communicate(timeout=30)
                self.assertEqual(first.returncode, code, output)
                deadline = time.monotonic() + 15
                while group_members(group) and time.monotonic() < deadline:
                    time.sleep(0.1)
                self.assertEqual(group_members(group), [], 'the test host stops with its invocation')


class SandboxCleanupTests(SandboxedTestCase):
    def test_cleanup_stops_the_sandbox_processes_and_their_groups_and_nothing_else(self):
        temp = sandbox(self, 'nailbiter-test-backend-cleanup-')
        (temp / 'sub').mkdir()
        sibling = Path(f'{temp}2')
        sibling.mkdir()
        self.addCleanup(shutil.rmtree, sibling, ignore_errors=True)

        def start(cwd, *args, **options):
            process = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)', *args], cwd=cwd, **options)
            self.addCleanup(process.wait)
            self.addCleanup(process.kill)
            return process

        leader = start(temp, process_group=0)
        nested = start(temp / 'sub')
        grouped = start(sibling, process_group=leader.pid)
        bystander = start(sibling)
        mentioner = start(sibling, str(temp / 'sub'))
        stop_processes_under(temp)
        for name, process in {'leader': leader, 'nested': nested, 'grouped': grouped}.items():
            with self.subTest(stopped=name):
                self.assertEqual(process.wait(timeout=10), -signal.SIGKILL)
        for name, process in {'bystander': bystander, 'mentioner': mentioner}.items():
            with self.subTest(survives=name):
                self.assertIsNone(process.poll())


def dotnet_sdk_root():
    if not shutil.which('dotnet'):
        return None
    with contextlib.suppress(OSError, subprocess.SubprocessError):
        sdks = subprocess.run(['dotnet', '--list-sdks'], cwd=ROOT, capture_output=True, text=True, timeout=60).stdout
        match = re.search(r'^10\.\S+ \[(.+)\]$', sdks, re.MULTILINE)
        if match:
            return Path(match[1]).parent
    return None


def test_packages():
    project = (ROOT / CSHARP).read_text()
    return ''.join(
        f'    <PackageReference Include="{name}" Version="{re.search(rf'Include="{re.escape(name)}" Version="([^"]+)"', project)[1]}" />\n'
        for name in ('Microsoft.NET.Test.Sdk', 'xunit', 'xunit.runner.visualstudio')
    )


def project_file(sources, references=''):
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
{sources}{references}  </ItemGroup>
</Project>
"""


def write_real_fixture(worktree):
    shared = '    <ProjectReference Include="../shared/Shared.csproj" />\n' + test_packages()
    files = {
        'apps/shared/Shared.csproj': project_file(''),
        'apps/shared/Answer.cs': 'namespace Shared;\n\npublic static class Answer\n{\n    public static int Value => 42;\n}\n',
        FSHARP: project_file('    <Compile Include="Tests.fs" />\n', shared),
        'apps/backend.fsharp.tests/Tests.fs': 'module Tests\n\nopen Xunit\n\n[<Fact>]\nlet ``reads the shared answer`` () =\n    Assert.Equal(42, Shared.Answer.Value)\n',
        CSHARP: project_file('', shared),
        'apps/backend.tests/Tests.cs': 'using Xunit;\n\npublic class Tests\n{\n    [Fact]\n    public void Reads_the_shared_answer() => Assert.Equal(42, Shared.Answer.Value);\n}\n',
    }
    for name, body in files.items():
        (worktree / name).parent.mkdir(parents=True, exist_ok=True)
        (worktree / name).write_text(body)


@unittest.skipIf(dotnet_sdk_root() is None, 'needs a .NET 10 SDK on PATH')
class RealToolchainTests(SandboxedTestCase):
    def test_concurrent_suites_share_one_build_of_their_library(self):
        temp = sandbox(self, 'nailbiter-test-backend-real-')
        worktree = make_worktree(temp, 'worktree')
        write_real_fixture(worktree)
        write_executable(temp / 'bin/mise', FAKE_MISE)
        environment = {
            **os.environ,
            'PATH': f'{dotnet_sdk_root()}:{temp / "bin"}:{os.environ["PATH"]}',
            'MSBUILDDISABLENODEREUSE': '1',
        }
        start = lambda suite: subprocess.Popen([sys.executable, SCRIPT, suite], cwd=worktree, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        fsharp = start('fsharp')
        lock = worktree / 'obj/test-backend/lock'
        deadline = time.monotonic() + 30
        while not (lock.exists() and lock.read_text().strip()) and time.monotonic() < deadline:
            time.sleep(0.05)
        csharp = start('csharp')
        outputs = [process.communicate(timeout=180)[0] for process in (fsharp, csharp)]
        self.assertEqual([fsharp.returncode, csharp.returncode], [0, 0], '\n'.join(outputs))
        self.assertRegex(outputs[0], r'fsharp\s+passed\s+1 passed, 0 failed')
        self.assertRegex(outputs[1], r'csharp\s+passed\s+1 passed, 0 failed')
        self.assertIn('waiting for scripts/test-backend fsharp', outputs[1])
        runs = {next(run.glob('*.trx')).stem: run for run in (worktree / 'obj/test-backend/runs').iterdir()}
        self.assertEqual(sorted(runs), ['csharp', 'fsharp'])
        assert_built_after(self, runs['csharp'] / 'build.log', runs['fsharp'] / 'fsharp.trx')
        build_logs = [(run / 'build.log').read_text() for run in runs.values()]
        compiles = [len(re.findall(r'^\s*\S*/csc\s.*/out:obj/Release/net10\.0/Shared\.dll', log, re.MULTILINE)) for log in build_logs]
        self.assertEqual(sum(compiles), 1, 'the shared library compiles once across both invocations')
        logs = '\n'.join(path.read_text() for path in (worktree / 'obj/test-backend/runs').glob('*/*.log'))
        self.assertNotRegex(logs, re.compile('mvid', re.IGNORECASE))

if __name__ == '__main__':
    unittest.main()
