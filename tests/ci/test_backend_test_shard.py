import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/ci/backend-test-shard.py'
spec = importlib.util.spec_from_file_location('backend_test_shard', SCRIPT)
shard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shard)

NAMES = [
    'backend.tests.Alpha.One',
    'backend.tests.Alpha.Two(value: "a.b(c)")',
    'backend.tests.Alpha.Two(value: "x")',
    'backend.tests.Beta.One',
    'backend.tests.Beta.Two',
    'backend.tests.Gamma.One',
    'backend.tests.Delta.One',
    'backend.tests.Delta.Two',
    'backend.tests.Delta.Three',
    'backend.tests.Delta.Four',
]

FAKE_DOTNET = textwrap.dedent('''\
    #!/usr/bin/env python3
    import json, os, sys
    args = sys.argv[1:]
    log = os.environ['FAKE_LOG']
    with open(log, 'a') as handle:
        handle.write(json.dumps(args) + '\\n')
    if '--list-tests' in args:
        print('Test run for backend.tests.dll')
        print('The following Tests are available:')
        for name in json.loads(os.environ['FAKE_NAMES']):
            print('    ' + name)
        sys.exit(0)
    target = args[args.index('--logger') + 1].split('LogFileName=', 1)[1]
    if os.environ.get('FAKE_NO_TRX') != '1':
        ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
        tests = ''.join(f'<UnitTest id="{i}"><TestMethod className="{c}" name="m"/></UnitTest>' for i, c in enumerate(json.loads(os.environ['FAKE_RAN'])))
        counters = ' '.join(f'{k}="{v}"' for k, v in json.loads(os.environ['FAKE_COUNTERS']).items())
        with open(target, 'w') as handle:
            handle.write(f'<TestRun xmlns="{ns}"><TestDefinitions>{tests}</TestDefinitions><ResultSummary><Counters {counters}/></ResultSummary></TestRun>')
    sys.exit(int(os.environ.get('FAKE_EXIT', '0')))
''')


class PartitionTests(unittest.TestCase):
    def test_every_class_lands_in_exactly_one_shard(self):
        classes = {shard.test_class(name) for name in NAMES}
        for count in range(1, 6):
            shards = shard.partition(NAMES, count)
            self.assertEqual(len(shards), count)
            self.assertEqual(set().union(*shards), classes)
            self.assertEqual(sum(len(s) for s in shards), len(classes))

    def test_partition_balances_test_counts_and_is_order_independent(self):
        shards = shard.partition(NAMES, 2)
        self.assertEqual(shards, shard.partition(list(reversed(NAMES)), 2))
        sizes = [sum(1 for name in NAMES if shard.test_class(name) in s) for s in shards]
        self.assertEqual(sorted(sizes), [5, 5])

    def test_theory_arguments_do_not_change_the_class(self):
        self.assertEqual(shard.test_class('backend.tests.Alpha.Two(value: "a.b(c)")'), 'backend.tests.Alpha')

    def test_names_outside_the_flat_namespace_are_rejected(self):
        for name in ['other.Alpha.One', 'backend.tests.Outer.Inner.One', 'backend.testsX.Alpha.One']:
            with self.subTest(name=name), self.assertRaises(SystemExit):
                shard.test_class(name)

    def test_filter_anchors_each_class_name(self):
        self.assertEqual(shard.test_filter({'backend.tests.Beta', 'backend.tests.Alpha'}),
                         'FullyQualifiedName~backend.tests.Alpha.|FullyQualifiedName~backend.tests.Beta.')


class ShardRunTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        bin_dir = Path(self.scratch.name) / 'bin'
        bin_dir.mkdir()
        dotnet = bin_dir / 'dotnet'
        dotnet.write_text(FAKE_DOTNET)
        dotnet.chmod(0o755)
        self.log = Path(self.scratch.name) / 'calls.jsonl'
        self.assigned = sorted(shard.partition(NAMES, 2)[0])
        self.env = dict(os.environ, PATH=f'{bin_dir}:{os.environ["PATH"]}', FAKE_LOG=str(self.log),
                        FAKE_NAMES=json.dumps(NAMES), FAKE_RAN=json.dumps(self.assigned),
                        FAKE_COUNTERS='{"total": 5, "executed": 5, "passed": 5, "failed": 0}')

    def run_shard(self, *args, **env):
        return subprocess.run([sys.executable, str(SCRIPT), *(args or ('0', '2'))], cwd=ROOT, env=dict(self.env, **env),
                              capture_output=True, text=True, check=False)

    def test_passing_shard_runs_only_its_classes_without_building(self):
        result = self.run_shard()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.log.read_text().splitlines()
        self.assertEqual(len(calls), 2)
        self.assertTrue(all('"--no-build"' in call and '"Release"' in call for call in calls))
        self.assertIn(json.dumps([shard.test_filter(set(self.assigned))])[1:-1], calls[1])

    def test_shard_fails_when_a_class_did_not_run(self):
        result = self.run_shard(FAKE_RAN=json.dumps(self.assigned[:-1]))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('did not run', result.stderr)

    def test_shard_fails_when_an_unassigned_class_ran(self):
        result = self.run_shard(FAKE_RAN=json.dumps(self.assigned + ['backend.tests.Elsewhere']))
        self.assertNotEqual(result.returncode, 0)

    def test_shard_fails_on_failed_tests_exit_code_or_missing_results(self):
        cases = [
            {'FAKE_COUNTERS': '{"total": 5, "executed": 5, "passed": 4, "failed": 1}'},
            {'FAKE_COUNTERS': '{"total": 5, "executed": 5, "passed": 5, "failed": 0, "error": 1}'},
            {'FAKE_COUNTERS': '{"total": 1, "executed": 0, "passed": 0, "failed": 0}'},
            {'FAKE_EXIT': '1'},
            {'FAKE_NO_TRX': '1'},
        ]
        for env in cases:
            with self.subTest(env=env):
                self.assertNotEqual(self.run_shard(**env).returncode, 0)

    def test_invalid_shard_index_is_rejected(self):
        for args in [('2', '2'), ('-1', '2')]:
            with self.subTest(args=args):
                self.assertNotEqual(self.run_shard(*args).returncode, 0)


if __name__ == '__main__':
    unittest.main()
