import collections
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

PROJECT = 'apps/backend.tests/backend.tests.csproj'
NAMESPACE = 'backend.tests'
TRX = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def test_class(name):
    test_class, _, _ = name.split('(', 1)[0].rpartition('.')
    if not test_class.startswith(NAMESPACE + '.') or '.' in test_class[len(NAMESPACE) + 1:]:
        raise SystemExit(f'Unexpected test name outside {NAMESPACE}: {name}')
    return test_class


def listed_tests(output):
    lines = output.splitlines()
    marker = 'The following Tests are available:'
    if marker not in [line.strip() for line in lines]:
        raise SystemExit('dotnet test --list-tests printed no test list')
    start = [line.strip() for line in lines].index(marker) + 1
    return [line.strip() for line in lines[start:] if line.startswith('    ')]


def partition(names, count):
    sizes = collections.Counter(test_class(name) for name in names)
    shards = [set() for _ in range(count)]
    loads = [0] * count
    for name in sorted(sizes, key=lambda c: (-sizes[c], c)):
        index = loads.index(min(loads))
        shards[index].add(name)
        loads[index] += sizes[name]
    return shards


def test_filter(classes):
    return '|'.join(f'FullyQualifiedName~{name}.' for name in sorted(classes))


def executed_classes(trx_path):
    root = ET.parse(trx_path).getroot()
    classes = {test.find('t:TestMethod', TRX).get('className') for test in root.iterfind('.//t:UnitTest', TRX)}
    counters = root.find('.//t:ResultSummary/t:Counters', TRX)
    return classes, {key: int(counters.get(key, '0')) for key in ['total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted']}


def dotnet_test(*arguments, capture=False):
    command = ['dotnet', 'test', PROJECT, '--no-build', '--configuration', 'Release', *arguments]
    return subprocess.run(command, check=False, text=True, capture_output=capture)


def main():
    index, count = int(sys.argv[1]), int(sys.argv[2])
    if not 0 <= index < count:
        raise SystemExit(f'Shard index {index} is outside 0..{count - 1}')
    listing = dotnet_test('--list-tests', capture=True)
    if listing.returncode != 0:
        print(listing.stdout, listing.stderr, sep='\n')
        raise SystemExit('Listing backend tests failed')
    names = listed_tests(listing.stdout)
    classes = partition(names, count)[index]
    if not classes:
        raise SystemExit(f'Shard {index + 1}/{count} has no test classes')
    expected = sum(1 for name in names if test_class(name) in classes)
    print(f'Shard {index + 1}/{count}: {len(classes)} classes, {expected} of {len(names)} listed tests')
    for name in sorted(classes):
        print(f'  {name}')
    sys.stdout.flush()
    with tempfile.TemporaryDirectory() as scratch:
        trx = Path(scratch) / 'shard.trx'
        result = dotnet_test('--filter', test_filter(classes), '--logger', f'trx;LogFileName={trx}')
        ran, counters = executed_classes(trx)
    if ran != classes:
        raise SystemExit(f'Shard did not run {sorted(classes - ran)} and ran unassigned {sorted(ran - classes)}')
    if result.returncode != 0 or counters['failed'] or counters['error'] or counters['timeout'] or counters['aborted']:
        raise SystemExit(f'Backend tests failed: exit {result.returncode}, {counters}')
    if counters['passed'] == 0:
        raise SystemExit('Shard passed no tests')


if __name__ == '__main__':
    main()
