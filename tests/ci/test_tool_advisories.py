import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/tool-advisories.yml'
spec = importlib.util.spec_from_file_location('watch_tool_advisories', ROOT / 'scripts/ci/watch-tool-advisories.py')
watch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(watch)
BUN, TRIVY = watch.TOOLS
REPOSITORY = 'scowalt/nailbiter'


def repository_advisory(ghsa, vulnerable_range, ecosystem='npm', name='bun', **extra):
    return {'ghsa_id': ghsa, 'cve_id': None, 'summary': f'{ghsa} summary', 'html_url': f'https://github.com/advisories/{ghsa}',
            'withdrawn_at': None, 'vulnerabilities': [{'package': {'ecosystem': ecosystem, 'name': name}, 'vulnerable_version_range': vulnerable_range}],
            **extra}


BUN_REPOSITORY_ADVISORIES = [
    repository_advisory('GHSA-cf8x-9m69-3gg3', '>= 1.3.14, < 1.4.4'),
    repository_advisory('GHSA-663w-cv8g-jj2p', '>= 1.0.21, < 1.4.4'),
    repository_advisory('GHSA-26q8-h657-pf84', '< 1.4.4'),
]
TRIVY_REPOSITORY_ADVISORIES = [
    repository_advisory('GHSA-69fq-xp46-6x23', '=0.69.4', 'go', 'github.com/aquasecurity/trivy', cve_id='CVE-2026-33634'),
    repository_advisory('GHSA-q3fv-x8vg-qqm4', '<0.71.0', 'go', 'github.com/aquasecurity/trivy'),
    repository_advisory('GHSA-xcq4-m2r3-cmrj', '< 0.51.2', 'go', 'github.com/aquasecurity/trivy'),
    repository_advisory('GHSA-aaaa-bbbb-cccc', '<0.35.0', 'actions', 'aquasecurity/trivy-action'),
]


class FakeServices:
    def __init__(self, versions, osv, repository_advisories, issues=()):
        self.versions, self.osv, self.repository_advisories, self.issues = versions, osv, repository_advisories, list(issues)
        self.requests, self.gh = [], []

    def fetch(self, url, body=None):
        self.requests.append((url, body))
        if url == watch.OSV_QUERY:
            return {'vulns': self.osv.get((body['package']['name'], body['version']), [])}
        repository = url.removeprefix(f'{watch.GITHUB_API}/repos/').split('/security-advisories')[0]
        page = int(url.rsplit('page=', 1)[1])
        return self.repository_advisories.get(repository, [])[(page - 1) * watch.PAGE_SIZE:page * watch.PAGE_SIZE]

    def run(self, command, capture_output=False, text=False, check=False):
        if command[0] != 'gh':
            return subprocess.CompletedProcess(command, 0, f'{command[0]} version {self.versions[command[0]]}\n', '')
        self.gh.append(command)
        if command[1:3] == ['issue', 'list']:
            return subprocess.CompletedProcess(command, 0, json.dumps(self.issues), '')
        return subprocess.CompletedProcess(command, 0, '', '')

    def main(self):
        with contextlib.redirect_stdout(io.StringIO()):
            return watch.main(REPOSITORY, fetch=self.fetch, run=self.run)

    def writes(self):
        return [command for command in self.gh if command[1:3] != ['issue', 'list']]


class VersionRangeTests(unittest.TestCase):
    def test_reads_every_range_spelling_the_upstream_advisories_use(self):
        cases = {
            ('1.4.2', '>= 1.3.14, < 1.4.4'): True, ('1.4.4', '>= 1.3.14, < 1.4.4'): False, ('1.3.13', '>= 1.3.14, < 1.4.4'): False,
            ('0.69.4', '=0.69.4'): True, ('0.69.5', '=0.69.4'): False, ('0.69.4', '0.69.4'): True,
            ('0.51.1', '< 0.51.2'): True, ('0.51.2', '< 0.51.2'): False, ('0.70.9', '<0.71.0'): True,
            ('1.2.0', '<= 1.2.0'): True, ('1.2.1', '<= 1.2.0'): False, ('2.0.0', '> 1.9.9'): True, ('1.10.0', '< 1.9.0'): False,
        }
        for (version, vulnerable_range), expected in cases.items():
            with self.subTest(version=version, vulnerable_range=vulnerable_range):
                self.assertIs(watch.in_range(version, vulnerable_range), expected)

    def test_a_range_it_cannot_read_is_an_error_not_a_miss(self):
        for vulnerable_range in ['', '< 1.4', '~1.4.0', '>= 1.0.0 || < 2.0.0', '< 1.4.4-canary.1']:
            with self.subTest(vulnerable_range=vulnerable_range), self.assertRaises(ValueError):
                watch.in_range('1.4.4', vulnerable_range)

    def test_an_unreadable_range_for_the_tool_is_reported_for_a_manual_check(self):
        [advisory] = watch.repository_advisories(BUN, '1.4.4', [repository_advisory('GHSA-xxxx-yyyy-zzzz', '~1.4.0')])
        self.assertEqual(advisory.id, 'GHSA-xxxx-yyyy-zzzz')
        self.assertIn('check by hand', advisory.summary)


class WatcherTests(unittest.TestCase):
    maxDiff = None

    def test_the_bun_advisories_that_osv_does_not_list_are_found_in_the_upstream_repository(self):
        services = FakeServices({'bun': '1.4.2', 'trivy': '0.76.2'}, {}, {'oven-sh/bun': BUN_REPOSITORY_ADVISORIES})
        self.assertEqual(services.main(), 0)
        [create] = services.writes()
        self.assertEqual(create[:5], ['gh', 'issue', 'create', '--repo', REPOSITORY])
        self.assertEqual(create[create.index('--title') + 1], 'Published advisories affect the pinned CI bun 1.4.2')
        body = create[create.index('--body') + 1]
        for ghsa in ['GHSA-cf8x-9m69-3gg3', 'GHSA-663w-cv8g-jj2p', 'GHSA-26q8-h657-pf84']:
            self.assertIn(f'[{ghsa}](https://github.com/advisories/{ghsa})', body)
        self.assertIn('scripts/ci/install-bun.sh', body)

    def test_fixed_versions_open_nothing(self):
        services = FakeServices({'bun': '1.4.4', 'trivy': '0.76.2'}, {},
                                {'oven-sh/bun': BUN_REPOSITORY_ADVISORIES, 'aquasecurity/trivy': TRIVY_REPOSITORY_ADVISORIES})
        self.assertEqual(services.main(), 0)
        self.assertEqual(services.gh, [])
        self.assertEqual([url for url, _ in services.requests], [
            watch.OSV_QUERY, 'https://api.github.com/repos/oven-sh/bun/security-advisories?state=published&per_page=100&page=1',
            watch.OSV_QUERY, 'https://api.github.com/repos/aquasecurity/trivy/security-advisories?state=published&per_page=100&page=1',
        ])
        self.assertEqual([body for _, body in services.requests if body], [
            {'package': {'ecosystem': 'npm', 'name': 'bun'}, 'version': '1.4.4'},
            {'package': {'ecosystem': 'Go', 'name': 'github.com/aquasecurity/trivy'}, 'version': '0.76.2'},
        ])

    def test_osv_and_repository_listings_of_one_advisory_count_once(self):
        osv = {('github.com/aquasecurity/trivy', '0.69.4'): [
            {'id': 'GO-2026-4919', 'aliases': ['GHSA-69fq-xp46-6x23', 'CVE-2026-33634'], 'summary': 'compromised release'},
            {'id': 'GHSA-q3fv-x8vg-qqm4', 'aliases': ['GO-2026-5983'], 'summary': 'other'},
        ]}
        services = FakeServices({'bun': '1.4.4', 'trivy': '0.69.4'}, osv, {'aquasecurity/trivy': TRIVY_REPOSITORY_ADVISORIES})
        services.main()
        [create] = services.writes()
        body = create[create.index('--body') + 1]
        self.assertEqual(body.count('\n- ['), 2)
        self.assertIn('[GO-2026-4919](https://osv.dev/vulnerability/GO-2026-4919)', body)
        self.assertNotIn('GHSA-aaaa-bbbb-cccc', body)

    def test_withdrawn_advisories_and_other_packages_are_ignored(self):
        advisories = [repository_advisory('GHSA-1111-2222-3333', '< 9.0.0', withdrawn_at='2026-01-01T00:00:00Z'),
                      repository_advisory('GHSA-4444-5555-6666', '< 9.0.0', 'npm', 'bun-types')]
        self.assertEqual(watch.repository_advisories(BUN, '1.4.4', advisories), [])

    def test_every_page_of_repository_advisories_is_read(self):
        many = [repository_advisory(f'GHSA-0000-0000-{index:04d}', '< 0.0.1') for index in range(watch.PAGE_SIZE)]
        services = FakeServices({'bun': '1.4.3', 'trivy': '0.76.2'}, {}, {'oven-sh/bun': many + BUN_REPOSITORY_ADVISORIES[2:]})
        services.main()
        self.assertIn('https://api.github.com/repos/oven-sh/bun/security-advisories?state=published&per_page=100&page=2',
                      [url for url, _ in services.requests])
        [create] = services.writes()
        self.assertIn('GHSA-26q8-h657-pf84', create[create.index('--body') + 1])

    def test_an_open_issue_gets_a_comment_only_for_advisories_it_does_not_list(self):
        title = 'Published advisories affect the pinned CI bun 1.4.2'
        listed = {'number': 7, 'title': title, 'body': '- [GHSA-cf8x-9m69-3gg3](x)\n', 'comments': [{'body': 'GHSA-663w-cv8g-jj2p'}]}
        lookalike = {'number': 8, 'title': title + ' (old)', 'body': '', 'comments': []}
        services = FakeServices({'bun': '1.4.2', 'trivy': '0.76.2'}, {}, {'oven-sh/bun': BUN_REPOSITORY_ADVISORIES}, [lookalike, listed])
        services.main()
        [comment] = services.writes()
        self.assertEqual(comment[:4], ['gh', 'issue', 'comment', '7'])
        body = comment[comment.index('--body') + 1]
        self.assertIn('GHSA-26q8-h657-pf84', body)
        self.assertNotIn('GHSA-cf8x-9m69-3gg3', body)
        self.assertNotIn('GHSA-663w-cv8g-jj2p', body)

        listed['body'] += body
        services.gh.clear()
        services.main()
        self.assertEqual(services.writes(), [])

    def test_reads_the_version_the_installed_binary_prints(self):
        outputs = {'bun': '1.4.4\n', 'trivy': 'Version: 0.76.2\nVulnerability DB:\n  Version: 2\n'}
        run = lambda command, **_: subprocess.CompletedProcess(command, 0, outputs[command[0]], '')
        self.assertEqual([watch.installed_version(tool, run) for tool in watch.TOOLS], ['1.4.4', '0.76.2'])


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.workflow = yaml.safe_load(WORKFLOW.read_text())
        self.job = self.workflow['jobs']['watch']

    def test_runs_daily_and_on_demand_in_the_canonical_repository_only(self):
        events = self.workflow.get('on', self.workflow.get(True))
        self.assertEqual(set(events), {'schedule', 'workflow_dispatch'})
        self.assertEqual(len(events['schedule']), 1)
        self.assertEqual(self.job['if'], "github.repository == 'scowalt/nailbiter'")

    def test_only_the_watch_job_can_write_and_only_issues(self):
        self.assertEqual(self.workflow['permissions'], {'contents': 'read'})
        self.assertEqual(self.job['permissions'], {'contents': 'read', 'issues': 'write'})
        self.assertNotIn('secrets.', json.dumps(self.workflow))
        self.assertNotIn('environment', self.job)

    def test_checks_the_versions_the_ci_install_scripts_install(self):
        runs = [step.get('run') for step in self.job['steps'] if 'run' in step]
        self.assertEqual(runs, ['sh scripts/ci/install-bun.sh', 'sh scripts/ci/install-trivy.sh', 'python3 scripts/ci/watch-tool-advisories.py'])
        self.assertEqual([tool.name for tool in watch.TOOLS], ['bun', 'trivy'])


if __name__ == '__main__':
    unittest.main()
