import fnmatch
import os
import re
import subprocess


def selected(paths):
    patterns = ['apps/www/**', '.github/workflows/**', 'scripts/ci/**', '.mise.toml', 'package.json', 'bun.lock', 'patches/**']
    return any(fnmatch.fnmatchcase(path, pattern) for path in paths for pattern in patterns)


def main():
    event = os.environ.get('GITHUB_EVENT_NAME')
    if event == 'workflow_dispatch':
        changed = True
    elif event == 'push':
        before = os.environ.get('BEFORE_SHA', '')
        after = os.environ.get('GITHUB_SHA', '')
        if not all(re.fullmatch('[0-9a-f]{40}', value) for value in [before, after]):
            raise SystemExit('Push comparison requires full commit identities')
        args = ['ls-tree', '-r', '--name-only', after] if before == '0' * 40 else ['diff', '--name-only', before, after]
        paths = subprocess.check_output(['git', *args], text=True).splitlines()
        changed = selected(paths)
    else:
        changed = False
    print('changed=' + str(changed).lower())


if __name__ == '__main__':
    main()
