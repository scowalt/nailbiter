import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
from urllib.parse import parse_qs, urlsplit

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / 'scripts/pr/pr-ci.py'
REPO = 'octo/widgets'
NUMBER = 12
SHA = 'a' * 40
NEW_SHA = 'b' * 40
MERGE_SHA = 'c' * 40
EXIT_CODES = {'passed': 0, 'merged': 0, 'queued': 10, 'pending': 11, 'error': 3, 'failed': 20, 'cancelled': 21, 'blocked': 22, 'superseded': 23, 'timeout': 24, 'incomplete': 12}
RULES = [
    {'type': 'required_status_checks', 'parameters': {'strict_required_status_checks_policy': False, 'required_status_checks': [{'context': 'Validate', 'integration_id': 15368}, {'context': 'Build and scan backend image', 'integration_id': 15368}]}, 'ruleset_source_type': 'Repository', 'ruleset_source': REPO, 'ruleset_id': 77},
    {'type': 'required_linear_history', 'ruleset_source_type': 'Repository', 'ruleset_source': REPO, 'ruleset_id': 77},
    {'type': 'non_fast_forward', 'ruleset_source_type': 'Repository', 'ruleset_source': REPO, 'ruleset_id': 77},
]
RULESETS = [{'id': 77, 'name': 'main-branch-protection', 'target': 'branch', 'source_type': 'Repository', 'source': REPO, 'enforcement': 'active'}]
RULESET_DETAIL = RULESETS[0] | {'bypass_actors': [], 'current_user_can_bypass': 'never', 'conditions': {'ref_name': {'include': ['~DEFAULT_BRANCH'], 'exclude': []}}}
SETTINGS = {'full_name': REPO, 'default_branch': 'main', 'allow_squash_merge': True, 'allow_merge_commit': False, 'allow_rebase_merge': False, 'allow_auto_merge': False}
NOT_FOUND = (404, {'message': 'Not Found', 'status': '404'})
FORBIDDEN = (403, {'message': 'Resource not accessible by personal access token', 'status': '403'})


def pull(state='OPEN', head=SHA, draft=False, merge_commit=None, auto=None, queue=None, mergeable='MERGEABLE'):
    return {'number': NUMBER, 'url': f'https://github.com/{REPO}/pull/{NUMBER}', 'state': state, 'isDraft': draft, 'headRefOid': head, 'baseRefName': 'main', 'mergeable': mergeable, 'mergeStateStatus': 'CLEAN', 'mergeCommit': {'oid': merge_commit} if merge_commit else None, 'autoMergeRequest': auto, 'mergeQueueEntry': queue}


REQUIRED_JOBS = ['Validate', 'Build and scan backend image']


def run(run_id, status='completed', conclusion='success', workflow=1, name='CI', sha=SHA, created='2026-11-12T10:00:00Z', event='pull_request'):
    return {'id': run_id, 'name': name, 'workflow_id': workflow, 'event': event, 'status': status, 'conclusion': conclusion if status == 'completed' else None, 'head_sha': sha, 'run_attempt': 1, 'run_number': run_id, 'created_at': created, 'html_url': f'https://github.com/{REPO}/actions/runs/{run_id}'}


def job(name, conclusion='success', status='completed'):
    return {'id': len(name), 'name': name, 'status': status, 'conclusion': conclusion if status == 'completed' else None}


AUTO = {'enabledAt': '2026-11-12T10:05:00Z', 'mergeMethod': 'SQUASH'}


class Process:
    def __init__(self, returncode, stdout='', stderr=''):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def http(status, body):
    reason = {200: 'OK', 403: 'Forbidden', 404: 'Not Found'}.get(status, 'Status')
    return Process(0 if status < 400 else 1, f'HTTP/2.0 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nX-Github-Media-Type: github.v3\r\n\r\n{json.dumps(body)}', '' if status < 400 else f'gh: {body["message"]} (HTTP {status})\n')


class FakeGitHub:
    def __init__(self, pulls, runs=None, api=None, merge=(0, ''), after_merge=None, graphql_denied=False, jobs=None):
        self.pulls = list(pulls)
        self.jobs = jobs or {}
        self.runs = {sha: list(sequence) for sha, sequence in (runs or {SHA: [[run(1)]]}).items()}
        self.api = {f'repos/{REPO}': (200, SETTINGS), f'repos/{REPO}/rules/branches/main': (200, RULES), f'repos/{REPO}/rulesets': (200, RULESETS), f'repos/{REPO}/rulesets/77': (200, RULESET_DETAIL), f'repos/{REPO}/branches/main/protection': NOT_FOUND} | (api or {})
        self.merge = merge
        self.after_merge = after_merge
        self.graphql_denied = graphql_denied
        self.calls = []

    @staticmethod
    def next(sequence):
        return sequence.pop(0) if len(sequence) > 1 else sequence[0]

    def __call__(self, argv):
        self.calls.append(list(argv))
        if argv[:2] == ['pr', 'merge']:
            if self.merge[0] == 0 and self.after_merge is not None:
                self.pulls = list(self.after_merge)
            return Process(self.merge[0], '', self.merge[1])
        if argv[:2] == ['api', 'graphql']:
            if self.graphql_denied:
                return Process(1, json.dumps({'errors': [{'type': 'FORBIDDEN', 'message': 'Resource not accessible by personal access token'}]}), 'GraphQL: Resource not accessible by personal access token (repository.pullRequest)\n')
            current = self.next(self.pulls)
            return current if isinstance(current, Process) else Process(0, json.dumps({'data': {'repository': {'pullRequest': current}}}), '')
        if argv[0] == 'api':
            target = urlsplit(next(argument for argument in argv[1:] if argument.startswith('repos/')))
            override = self.api.get('runs' if target.path == f'repos/{REPO}/actions/runs' else target.path)
            if isinstance(override, Process):
                return override
            if target.path == f'repos/{REPO}/actions/runs':
                if 'runs' in self.api:
                    return http(*self.api['runs'])
                sha = parse_qs(target.query)['head_sha'][0]
                runs = self.next(self.runs[sha]) if sha in self.runs else []
                return http(200, {'total_count': len(runs), 'workflow_runs': runs})
            if target.path.startswith(f'repos/{REPO}/actions/runs/') and target.path.endswith('/jobs') and target.path not in self.api:
                entries = self.jobs.get(int(target.path.split('/')[-2]), REQUIRED_JOBS)
                return http(200, {'total_count': len(entries), 'jobs': [job(entry) if isinstance(entry, str) else entry for entry in entries]})
            return http(*self.api.get(target.path, NOT_FOUND))
        raise AssertionError(f'unexpected gh invocation {argv}')

    def merges(self):
        return [call for call in self.calls if call[:2] == ['pr', 'merge']]

    def mutating_calls(self):
        return [call for call in self.calls if call[:2] == ['pr', 'merge'] or (call[0] == 'api' and any(flag in call for flag in ['-X', '--method'])) or any('mutation' in argument for argument in call)]


def raw(status, text):
    return Process(0 if status < 400 else 1, f'HTTP/2.0 {status} OK\r\nContent-Type: application/json\r\n\r\n{text}', '')


class Clock:
    def __init__(self):
        self.value = 1000.0
        self.sleeps = []

    def now(self):
        return self.value

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.value += seconds


def load_tool():
    spec = importlib.util.spec_from_file_location('pr_ci', TOOL)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ToolTests(unittest.TestCase):
    def setUp(self):
        patcher = mock.patch('time.sleep', side_effect=AssertionError('real sleep used'))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.clock = Clock()

    def invoke(self, fake, *argv):
        out = io.StringIO()
        err = io.StringIO()
        code = load_tool().main(['--repo', REPO, *argv], run=fake, clock=self.clock.now, sleep=self.clock.sleep, out=out, err=err)
        self.err = err.getvalue()
        result = json.loads(out.getvalue()) if out.getvalue() else None
        if result is not None and 'result' in result:
            self.assertEqual(code, EXIT_CODES[result['result']], result)
            self.assertIn(result['result'], self.err.splitlines()[-1])
        return code, result

    def progress_lines(self):
        return self.err.splitlines()[:-1]

    def assert_read_only(self, fake):
        self.assertEqual(fake.mutating_calls(), [])
        self.assertTrue(fake.calls)

    def test_status_of_pending_ci_is_read_only_and_pinned_to_the_head(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')]]})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'pending')
        self.assertEqual(result['expected_sha'], SHA)
        self.assertFalse(result['mutated'])
        self.assertEqual(self.clock.sleeps, [])
        self.assert_read_only(fake)
        runs_queries = [call for call in fake.calls if any('actions/runs' in argument for argument in call)]
        self.assertTrue(runs_queries and all(any(f'head_sha={SHA}' in argument for argument in call) for call in runs_queries))

    def test_passed_ci_is_labelled_actions_only(self):
        fake = FakeGitHub([pull()])
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'passed')
        self.assertEqual(result['ci']['evidence'], 'actions-only')
        self.assertIn('Actions-only', self.err)
        self.assert_read_only(fake)

    def test_failing_ci_stops_a_merge_without_mutation(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')], [run(1, conclusion='failure')]]})
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'failed')
        self.assertFalse(result['mutated'])
        self.assertEqual(fake.merges(), [])

    def test_cancelled_ci_is_its_own_result(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, conclusion='cancelled')]]})
        code, result = self.invoke(fake, 'wait', str(NUMBER), '--sha', SHA)
        self.assertEqual(result['result'], 'cancelled')
        self.assert_read_only(fake)

    def test_a_newer_run_supersedes_a_cancelled_run_of_the_same_workflow(self):
        runs = [run(1, conclusion='cancelled', created='2026-11-12T10:00:00Z'), run(2, created='2026-11-12T10:30:00Z'), run(3, workflow=2, name='Docs')]
        fake = FakeGitHub([pull()], runs={SHA: [runs]})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'passed')
        self.assertEqual(result['ci']['superseded_runs'], [1])
        self.assertEqual(sorted(entry['id'] for entry in result['ci']['runs']), [2, 3])

    def test_a_newer_pending_run_keeps_waiting_instead_of_reporting_its_cancelled_predecessor(self):
        runs = [run(1, conclusion='cancelled', created='2026-11-12T10:00:00Z'), run(2, status='queued', created='2026-11-12T10:30:00Z')]
        fake = FakeGitHub([pull()], runs={SHA: [runs]})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'pending')

    def test_runs_for_another_sha_are_ignored(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, conclusion='failure', workflow=2, sha=NEW_SHA), run(2)]]})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'passed')

    def test_a_head_that_changes_while_waiting_supersedes_the_revision(self):
        fake = FakeGitHub([pull(), pull(head=NEW_SHA)], runs={SHA: [[run(1, status='in_progress')]], NEW_SHA: [[run(2)]]})
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'superseded')
        self.assertEqual(result['head_sha'], NEW_SHA)
        self.assertEqual(fake.merges(), [])

    def test_the_head_is_reread_immediately_before_merging(self):
        fake = FakeGitHub([pull(), pull(head=NEW_SHA)], after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'superseded')
        self.assertFalse(result['mutated'])
        self.assertEqual(fake.merges(), [])

    def test_an_expected_sha_that_is_not_the_head_never_merges(self):
        fake = FakeGitHub([pull(head=NEW_SHA)], runs={NEW_SHA: [[run(1, sha=NEW_SHA)]]}, after_merge=[pull(state='MERGED', head=NEW_SHA, merge_commit=MERGE_SHA)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'superseded')
        self.assertEqual(fake.merges(), [])

    def test_merge_requires_the_explicit_opt_in_and_the_expected_sha(self):
        for argv in [['merge', str(NUMBER), '--sha', SHA], ['merge', str(NUMBER), '--confirm-merge'], ['merge', str(NUMBER), '--sha', 'main', '--confirm-merge'], ['merge', str(NUMBER), '--sha', SHA[:12], '--confirm-merge']]:
            with self.subTest(argv=argv):
                fake = FakeGitHub([pull()], after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
                with mock.patch('sys.stderr', io.StringIO()):
                    with self.assertRaises(SystemExit) as raised:
                        self.invoke(fake, *argv)
                self.assertEqual(raised.exception.code, 2)
                self.assertEqual(fake.calls, [])

    def test_a_confirmed_merge_is_pinned_and_read_back(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')], [run(1)]]}, after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'merged')
        self.assertEqual(result['merge_commit'], MERGE_SHA)
        self.assertTrue(result['mutated'])
        self.assertEqual(fake.merges(), [['pr', 'merge', str(NUMBER), '--repo', REPO, '--squash', '--match-head-commit', SHA]])
        self.assertTrue(self.clock.sleeps)
        self.assertGreater(fake.calls.index(fake.merges()[0]), max(index for index, call in enumerate(fake.calls) if any('actions/runs' in argument for argument in call)))

    def test_merged_needs_a_merge_commit_read_back(self):
        fake = FakeGitHub([pull()], after_merge=[pull(state='MERGED'), pull(state='MERGED', merge_commit=MERGE_SHA)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'merged')
        self.assertEqual(result['merge_commit'], MERGE_SHA)

    def test_a_merge_that_is_never_read_back_times_out_instead_of_claiming_success(self):
        fake = FakeGitHub([pull()], after_merge=[pull()])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'timeout')
        self.assertTrue(result['mutated'])
        self.assertNotIn('merge_commit', result)

    def test_a_merge_command_that_only_queues_is_reported_as_queued(self):
        fake = FakeGitHub([pull()], after_merge=[pull(queue={'state': 'QUEUED', 'position': 1})])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'queued')
        self.assertNotEqual(code, EXIT_CODES['merged'])
        self.assertNotIn('merge_commit', result)

    def test_a_pr_that_is_already_queued_is_not_merged_again(self):
        fake = FakeGitHub([pull(auto=AUTO)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'queued')
        self.assertEqual(fake.merges(), [])

    def test_unsupported_auto_merge_is_reported_without_mutation(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')]]})
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
        self.assertEqual(result['result'], 'blocked')
        self.assertIn('auto-merge', result['reason'])
        self.assertEqual(result['policy']['auto_merge'], 'disabled')
        self.assertEqual(fake.merges(), [])

    def test_supported_auto_merge_is_queued_pinned_to_the_head(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')]]}, api={f'repos/{REPO}': (200, SETTINGS | {'allow_auto_merge': True})}, after_merge=[pull(auto=AUTO)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
        self.assertEqual(result['result'], 'queued')
        self.assertEqual(fake.merges(), [['pr', 'merge', str(NUMBER), '--repo', REPO, '--auto', '--squash', '--match-head-commit', SHA]])

    def test_a_disallowed_merge_method_is_blocked_before_mutation(self):
        fake = FakeGitHub([pull()])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--method', 'rebase')
        self.assertEqual(result['result'], 'blocked')
        self.assertEqual(fake.merges(), [])

    def test_draft_closed_and_conflicting_prs_are_blocked(self):
        for state in [pull(draft=True), pull(state='CLOSED'), pull(mergeable='CONFLICTING')]:
            with self.subTest(state=state):
                fake = FakeGitHub([state])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'blocked')
                self.assertEqual(fake.merges(), [])

    def test_a_merge_github_rejects_is_blocked_with_its_reason(self):
        fake = FakeGitHub([pull()], merge=(1, 'GraphQL: Repository rule violations found (mergePullRequest)\n'))
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'blocked')
        self.assertIn('Repository rule violations', result['reason'])
        self.assertTrue(result['mutated'])
        self.assertEqual(len(fake.merges()), 1)
        self.assertFalse(any('--admin' in call for call in fake.merges()))

    def test_a_merge_rejected_because_the_head_moved_is_superseded(self):
        fake = FakeGitHub([pull(), pull(), pull(head=NEW_SHA)], merge=(1, 'GraphQL: Head branch was modified. Review and try the merge again. (mergePullRequest)\n'))
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'superseded')

    def test_waiting_is_bounded_backs_off_and_prints_only_transitions(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')]]})
        code, result = self.invoke(fake, 'wait', str(NUMBER), '--sha', SHA, '--timeout', '600')
        self.assertEqual(result['result'], 'timeout')
        self.assertAlmostEqual(sum(self.clock.sleeps), 600)
        self.assertGreater(self.clock.sleeps[1], self.clock.sleeps[0])
        self.assertTrue(all(later >= earlier for earlier, later in zip(self.clock.sleeps[:-2], self.clock.sleeps[1:-1])))
        self.assertLessEqual(max(self.clock.sleeps), 120)
        self.assertEqual(len(self.progress_lines()), 1)
        self.assert_read_only(fake)

    def test_progress_is_printed_once_per_state_change(self):
        runs = [[], [run(1, status='queued')], [run(1, status='queued')], [run(1, status='in_progress')], [run(1, status='in_progress')], [run(1)]]
        fake = FakeGitHub([pull()], runs={SHA: runs})
        code, result = self.invoke(fake, 'wait', str(NUMBER), '--sha', SHA)
        self.assertEqual(result['result'], 'passed')
        self.assertEqual(len(self.progress_lines()), 3)

    def test_wait_reports_a_merge_done_elsewhere(self):
        fake = FakeGitHub([pull(), pull(state='MERGED', merge_commit=MERGE_SHA)], runs={SHA: [[run(1, status='in_progress')]]})
        code, result = self.invoke(fake, 'wait', str(NUMBER), '--sha', SHA)
        self.assertEqual(result['result'], 'merged')
        self.assert_read_only(fake)

    def test_rulesets_with_a_legacy_404_are_reported_as_protected(self):
        fake = FakeGitHub([pull()])
        code, result = self.invoke(fake, 'policy')
        self.assertEqual(code, 0)
        self.assertNotIn('result', result)
        policy = result['policy']
        self.assertEqual(policy['protection'], 'rulesets')
        self.assertEqual(policy['legacy_protection']['http_status'], 404)
        self.assertEqual(policy['required_checks'], ['Validate', 'Build and scan backend image'])
        self.assertEqual(policy['rulesets'][0]['current_user_can_bypass'], 'never')
        self.assertEqual(policy['merge_methods'], {'squash': True, 'merge': False, 'rebase': False})
        self.assertNotIn('unprotected', json.dumps(result))
        self.assert_read_only(fake)

    def test_a_legacy_404_without_rules_is_unknown_not_unprotected(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}/rules/branches/main': (200, []), f'repos/{REPO}/rulesets': (200, [])})
        code, result = self.invoke(fake, 'policy')
        self.assertEqual(result['policy']['protection'], 'unknown')
        self.assertNotIn('unprotected', json.dumps(result))

    def test_legacy_protection_alone_is_reported(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}/rules/branches/main': (200, []), f'repos/{REPO}/rulesets': (200, []), f'repos/{REPO}/branches/main/protection': (200, {'required_linear_history': {'enabled': True}})})
        code, result = self.invoke(fake, 'policy')
        self.assertEqual(result['policy']['protection'], 'legacy')

    def test_denied_policy_access_is_unknown(self):
        for denied in [FORBIDDEN, NOT_FOUND]:
            with self.subTest(denied=denied):
                fake = FakeGitHub([pull()], api={f'repos/{REPO}': denied, f'repos/{REPO}/rules/branches/main': denied, f'repos/{REPO}/rulesets': denied, f'repos/{REPO}/branches/main/protection': denied})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                policy = result['policy']
                self.assertEqual(policy['protection'], 'unknown')
                self.assertEqual(policy['auto_merge'], 'unknown')
                self.assertIsNone(policy['merge_methods'])
                self.assertEqual(policy['access']['rulesets'], denied[0])
                self.assertEqual(result['result'], 'incomplete')

    def test_denied_pull_request_or_actions_access_is_an_error(self):
        for fake in [FakeGitHub([pull()], graphql_denied=True), FakeGitHub([pull()], api={'runs': FORBIDDEN})]:
            with self.subTest(calls=fake.api.get('runs')):
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'error')
                self.assertIn('not accessible', result['reason'])
                self.assertEqual(fake.merges(), [])

    def test_malformed_json_from_gh_is_an_error_not_a_traceback(self):
        cases = {
            'graphql body': FakeGitHub([Process(0, '{not json')]),
            'graphql without data': FakeGitHub([Process(0, json.dumps({'data': None}))]),
            'pull request without fields': FakeGitHub([Process(0, json.dumps({'data': {'repository': {'pullRequest': {'number': NUMBER}}}}))]),
            'runs body': FakeGitHub([pull()], api={'runs': raw(200, '<html>oops')}),
            'runs without workflow_runs': FakeGitHub([pull()], api={'runs': (200, {'message': 'x'})}),
            'run without fields': FakeGitHub([pull()], api={'runs': (200, {'workflow_runs': [{'id': 1}]})}),
            'jobs body': FakeGitHub([pull()], api={f'repos/{REPO}/actions/runs/1/jobs': raw(200, 'garbage')}),
        }
        for label, fake in cases.items():
            with self.subTest(label):
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'error')
                self.assertEqual(code, 3)
                self.assertFalse(result['mutated'])
                self.assertEqual(fake.merges(), [])

    def test_malformed_policy_bodies_are_unknown(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}': raw(200, 'garbage'), f'repos/{REPO}/rules/branches/main': raw(200, '[1, 2]'), f'repos/{REPO}/rulesets': (200, [{'name': 'no id'}])})
        code, result = self.invoke(fake, 'policy')
        self.assertEqual(result['policy']['auto_merge'], 'unknown')
        self.assertIsNone(result['policy']['merge_methods'])
        self.assertIsNone(result['policy']['required_checks'])
        self.assertEqual(result['policy']['protection'], 'unknown')

    def test_an_unreadable_readback_after_merging_reports_the_merge_call(self):
        for label, readback in [('malformed', Process(0, '{not json')), ('denied', Process(1, '', 'GraphQL: Resource not accessible by personal access token\n'))]:
            with self.subTest(label):
                fake = FakeGitHub([pull()], after_merge=[readback])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'error')
                self.assertTrue(result['mutated'])
                self.assertIn('gh pr merge was called', result['reason'])
                self.assertIn('outcome is unknown', result['reason'])
                self.assertIn('outcome is unknown', self.err.splitlines()[-1])
                self.assertEqual(len(fake.merges()), 1)

    def test_runs_of_one_workflow_from_different_events_both_count(self):
        runs = [run(1, conclusion='failure', event='push', created='2026-11-12T10:00:00Z'), run(2, created='2026-11-12T10:00:01Z')]
        fake = FakeGitHub([pull()], runs={SHA: [runs]})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'failed')
        self.assertEqual(sorted(entry['id'] for entry in result['ci']['runs']), [1, 2])

    def test_required_checks_without_an_actions_job_stay_pending(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(9, workflow=9, name='PR Title')]]}, jobs={9: ['Check title']})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'pending')
        self.assertIn('Validate', result['reason'])
        self.assertEqual(result['ci']['required_checks'], {'Validate': [], 'Build and scan backend image': []})

    def test_waiting_ends_when_every_required_check_has_a_passing_job(self):
        sequence = [[run(9, workflow=9, name='PR Title')], [run(9, workflow=9, name='PR Title'), run(10, status='in_progress')], [run(9, workflow=9, name='PR Title'), run(10)]]
        fake = FakeGitHub([pull()], runs={SHA: sequence}, jobs={9: ['Check title'], 10: ['Validate', 'Build and scan backend image', 'Release production']})
        code, result = self.invoke(fake, 'wait', str(NUMBER), '--sha', SHA)
        self.assertEqual(result['result'], 'passed')
        self.assertEqual(result['ci']['required_checks'], {'Validate': [{'run_id': 10, 'conclusion': 'success'}], 'Build and scan backend image': [{'run_id': 10, 'conclusion': 'success'}]})
        self.assertTrue(result['ci']['complete'])

    def test_passed_without_readable_required_checks_is_incomplete(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}/rules/branches/main': FORBIDDEN})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'incomplete')
        self.assertNotEqual(code, EXIT_CODES['passed'])
        self.assertFalse(result['ci']['complete'])
        self.assertIsNone(result['ci']['required_checks'])
        self.assertIn('incomplete', result['reason'])
        self.assertNotIn('still pending', result['reason'])

    def test_no_runs_are_pending_when_the_required_checks_are_known(self):
        for rules, required in [(RULES[1:], []), (RULES, REQUIRED_JOBS)]:
            with self.subTest(required=required):
                fake = FakeGitHub([pull()], runs={SHA: [[]]}, api={f'repos/{REPO}/rules/branches/main': (200, rules)})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertEqual(result['policy']['required_checks'], required)
                self.assertEqual(result['result'], 'pending')
                self.assertEqual(result['reason'], f'no Actions runs yet for {SHA[:12]}')

    def test_no_runs_with_unknown_required_checks_are_incomplete(self):
        fake = FakeGitHub([pull()], runs={SHA: [[]]}, api={f'repos/{REPO}/rules/branches/main': FORBIDDEN})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'incomplete')
        self.assertFalse(result['ci']['complete'])
        self.assertIn('no Actions runs yet', result['reason'])
        self.assertIn('incomplete', result['reason'])

    def test_required_check_names_match_exactly(self):
        for name in ['Validate (extra)', 'Validate (3.12)', 'validate', 'Pre-Validate', 'Validate ', ' Validate']:
            with self.subTest(name=name):
                fake = FakeGitHub([pull()], jobs={1: [name, 'Build and scan backend image']})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertEqual(result['result'], 'pending')
                self.assertEqual(result['ci']['required_checks']['Validate'], [])

    def test_a_required_job_decides_even_when_its_run_succeeds(self):
        for conclusion, expected in [('failure', 'failed'), ('timed_out', 'failed'), ('cancelled', 'cancelled'), ('skipped', 'passed')]:
            with self.subTest(conclusion=conclusion):
                fake = FakeGitHub([pull()], jobs={1: [job('Validate', conclusion), 'Build and scan backend image']}, after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
                code, result = self.invoke(fake, 'merge' if expected != 'passed' else 'status', str(NUMBER), *(['--sha', SHA, '--confirm-merge'] if expected != 'passed' else []))
                self.assertEqual(result['result'], expected)
                self.assertEqual(result['ci']['required_checks']['Validate'], [{'run_id': 1, 'conclusion': conclusion}])
                self.assertEqual(fake.merges(), [])
                if expected != 'passed':
                    self.assertIn(f'Validate {conclusion}', result['reason'])

    def test_a_required_job_in_every_matching_run_counts(self):
        runs = [run(1, event='push'), run(2)]
        fake = FakeGitHub([pull()], runs={SHA: [runs]}, jobs={1: [job('Validate', 'failure'), 'Build and scan backend image'], 2: REQUIRED_JOBS})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'failed')
        self.assertEqual(len(result['ci']['required_checks']['Validate']), 2)

    def test_malformed_jobs_are_an_error(self):
        for label, entry in [('no name', {'id': 1, 'status': 'completed', 'conclusion': 'success'}), ('conclusion not a string', job('Validate') | {'conclusion': 7}), ('no status', {'id': 1, 'name': 'Validate', 'conclusion': 'success'}), ('not an object', ['Validate'])]:
            with self.subTest(label):
                fake = FakeGitHub([pull()], jobs={1: [entry, 'Build and scan backend image']})
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'error')
                self.assertEqual(fake.merges(), [])

    def test_a_readback_with_wrong_nested_types_reports_the_merge_call(self):
        cases = {
            'merge commit oid': pull(state='MERGED') | {'mergeCommit': {'oid': 5}},
            'state': pull() | {'state': None},
            'head': pull() | {'headRefOid': 123},
            'auto-merge method': pull(auto={'enabledAt': '2026-11-12T10:05:00Z', 'mergeMethod': None}),
            'queue state': pull(queue={'state': 3, 'position': 1}),
            'draft flag': pull() | {'isDraft': 'no'},
            'base branch': pull() | {'baseRefName': 5},
            'mergeable': pull() | {'mergeable': None},
            'missing merge commit key': {key: value for key, value in pull(state='MERGED', merge_commit=MERGE_SHA).items() if key != 'mergeCommit'},
            'missing auto-merge key': {key: value for key, value in pull().items() if key != 'autoMergeRequest'},
            'missing merge queue key': {key: value for key, value in pull().items() if key != 'mergeQueueEntry'},
        }
        for label, readback in cases.items():
            with self.subTest(label):
                fake = FakeGitHub([pull()], after_merge=[readback])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'error')
                self.assertTrue(result['mutated'])
                self.assertIn('outcome is unknown', result['reason'])
                self.assertEqual(len(fake.merges()), 1)

    def test_failed_outranks_cancelled(self):
        runs = [run(1, conclusion='cancelled'), run(2, conclusion='failure', workflow=2, name='Docs')]
        for order in [runs, runs[::-1]]:
            with self.subTest(first=order[0]['conclusion']):
                fake = FakeGitHub([pull()], runs={SHA: [order]})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertEqual(result['result'], 'failed')
                self.assertEqual(code, EXIT_CODES['failed'])

    def test_an_unfinished_required_job_is_named_sensibly(self):
        fake = FakeGitHub([pull()], jobs={1: [job('Validate', status='in_progress'), 'Build and scan backend image']})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'pending')
        self.assertIn('Validate not finished', result['reason'])
        self.assertNotIn('None', result['reason'])

    def legacy_only(self, protection, jobs):
        return FakeGitHub([pull()], jobs=jobs, api={
            f'repos/{REPO}/rules/branches/main': (200, []),
            f'repos/{REPO}/rulesets': (200, []),
            f'repos/{REPO}/branches/main/protection': (200, protection),
        })

    def test_legacy_required_checks_are_waited_for(self):
        shapes = {
            'contexts': {'required_status_checks': {'strict': True, 'contexts': ['Validate', 'Legacy gate'], 'checks': []}},
            'checks': {'required_status_checks': {'strict': True, 'contexts': [], 'checks': [{'context': 'Validate', 'app_id': 15368}, {'context': 'Legacy gate', 'app_id': None}]}},
        }
        for label, protection in shapes.items():
            with self.subTest(label):
                code, result = self.invoke(self.legacy_only(protection, {1: ['Validate']}), 'status', str(NUMBER))
                self.assertEqual(result['policy']['required_checks'], ['Validate', 'Legacy gate'])
                self.assertEqual(result['result'], 'pending')
                self.assertIn('Legacy gate', result['reason'])
                self.assertTrue(result['ci']['complete'])
                fake = self.legacy_only(protection, {1: ['Validate', 'Legacy gate']})
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(len(fake.merges()), 1)
                self.assertEqual(result['ci']['required_checks']['Legacy gate'], [{'run_id': 1, 'conclusion': 'success'}])

    def test_legacy_protection_without_required_checks_adds_none(self):
        for protection in [{'required_linear_history': {'enabled': True}}, {'required_status_checks': None}]:
            with self.subTest(protection=protection):
                code, result = self.invoke(self.legacy_only(protection, {1: []}), 'status', str(NUMBER))
                self.assertEqual(result['policy']['required_checks'], [])
                self.assertEqual(result['result'], 'passed')

    def test_unreadable_legacy_required_checks_are_incomplete(self):
        cases = [
            {'required_status_checks': {'contexts': 'Validate'}},
            {'required_status_checks': {'checks': [{'context': 5}]}},
            {'required_status_checks': 'Validate'},
            ['not an object'],
        ]
        for protection in cases:
            with self.subTest(protection=protection):
                code, result = self.invoke(self.legacy_only(protection, {1: REQUIRED_JOBS}), 'status', str(NUMBER))
                self.assertIsNone(result['policy']['required_checks'])
                self.assertEqual(result['result'], 'incomplete')
                self.assertEqual(code, EXIT_CODES['incomplete'])
                for auto in [[], ['--auto']]:
                    fake = self.legacy_only(protection, {1: REQUIRED_JOBS})
                    code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', *auto)
                    self.assertNotIn(result['result'], ['merged', 'queued'])
                    self.assertEqual(fake.merges(), [])

    def test_a_legacy_response_other_than_200_or_404_is_incomplete(self):
        cases = {
            'forbidden': FORBIDDEN,
            'unauthorized': (401, {'message': 'Bad credentials', 'status': '401'}),
            'unprocessable': (422, {'message': 'Validation Failed', 'status': '422'}),
            'rate limited': (429, {'message': 'API rate limit exceeded', 'status': '429'}),
            'server error with JSON': (500, {'message': 'Server Error', 'status': '500'}),
            'bad gateway with HTML': raw(502, '<html><body>Bad Gateway</body></html>'),
            'transport failure': Process(1, '', 'error connecting to api.github.com\n'),
            'no status': Process(0, '', ''),
        }
        auto_enabled = {f'repos/{REPO}': (200, SETTINGS | {'allow_auto_merge': True})}
        for label, legacy in cases.items():
            with self.subTest(label):
                api = {f'repos/{REPO}/branches/main/protection': legacy}
                code, result = self.invoke(FakeGitHub([pull()], api=api), 'status', str(NUMBER))
                self.assertEqual(result['result'], 'incomplete')
                self.assertEqual(code, 12)
                self.assertIsNone(result['policy']['required_checks'])
                self.assertFalse(result['ci']['complete'])
                for extra, auto in [({}, []), (auto_enabled, ['--auto'])]:
                    fake = FakeGitHub([pull()], api=api | extra, after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
                    code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', *auto)
                    self.assertEqual(result['result'], 'incomplete')
                    self.assertEqual(code, 12)
                    self.assertFalse(result['mutated'])
                    self.assertEqual(fake.merges(), [])

    def test_merge_auto_never_arms_auto_merge_on_incomplete_evidence(self):
        for protection in [{'required_status_checks': {'contexts': 'Validate'}}, ['not an object']]:
            with self.subTest(protection=protection):
                fake = self.legacy_only(protection, {1: REQUIRED_JOBS})
                fake.api[f'repos/{REPO}'] = (200, SETTINGS | {'allow_auto_merge': True})
                fake.after_merge = [pull(auto=AUTO)]
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
                self.assertEqual(result['policy']['auto_merge'], 'enabled')
                self.assertEqual(result['policy']['bypass'], 'never')
                self.assertEqual(result['result'], 'incomplete')
                self.assertEqual(code, 12)
                self.assertFalse(result['mutated'])
                self.assertEqual(fake.merges(), [])

    def test_merge_auto_with_pending_runs_never_arms_auto_merge_when_the_required_checks_are_unknown(self):
        unknown = {
            'legacy 403': FORBIDDEN,
            'legacy 502 html': raw(502, '<html><body>Bad Gateway</body></html>'),
            'legacy transport fail': Process(1, '', 'error connecting to api.github.com\n'),
            'legacy 200 bad shape': (200, {'required_status_checks': {'contexts': 'Validate'}}),
        }
        for label, legacy in unknown.items():
            with self.subTest(label):
                fake = self.pending_then_passing_auto_merge(legacy)
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
                self.assertEqual((result['result'], code), ('incomplete', 12))
                self.assertEqual(result['policy']['bypass'], 'never')
                self.assertFalse(result['ci']['complete'])
                self.assertFalse(result['mutated'])
                self.assertEqual(fake.merges(), [])

    def test_merge_auto_with_pending_runs_arms_auto_merge_when_the_required_checks_are_known(self):
        fake = self.pending_then_passing_auto_merge(NOT_FOUND)
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
        self.assertEqual((result['result'], code), ('queued', 10))
        self.assertTrue(result['ci']['complete'])
        self.assertTrue(result['mutated'])
        self.assertEqual(fake.merges(), [['pr', 'merge', str(NUMBER), '--repo', REPO, '--auto', '--squash', '--match-head-commit', SHA]])

    def pending_then_passing_auto_merge(self, legacy):
        return FakeGitHub(
            [pull()],
            runs={SHA: [[run(1, status='in_progress')], [run(1)]]},
            api={f'repos/{REPO}': (200, SETTINGS | {'allow_auto_merge': True}), f'repos/{REPO}/branches/main/protection': legacy},
            after_merge=[pull(auto=AUTO)],
        )

    def merged_elsewhere(self, legacy, pulls):
        return FakeGitHub(
            pulls,
            api={f'repos/{REPO}': (200, SETTINGS | {'allow_auto_merge': True}), f'repos/{REPO}/branches/main/protection': legacy},
            after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)],
        )

    def test_a_pull_request_merged_before_the_reread_is_never_merged_again(self):
        cases = [
            ('known', NOT_FOUND, [], ('pending', 11)),
            ('known', NOT_FOUND, ['--auto'], ('pending', 11)),
            ('unknown', FORBIDDEN, [], ('incomplete', 12)),
            ('unknown', FORBIDDEN, ['--auto'], ('pending', 11)),
        ]
        for label, legacy, auto, expected in cases:
            with self.subTest(label, auto=auto):
                fake = self.merged_elsewhere(legacy, [pull(), pull(state='MERGED')])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', *auto)
                self.assertEqual((result['result'], code), expected)
                self.assertEqual(result['ci']['complete'], label == 'known')
                self.assertFalse(result['mutated'])
                self.assertEqual(fake.merges(), [])
                if expected[0] == 'pending':
                    self.assertEqual(result['pull_request']['state'], 'MERGED')
                    self.assertIn('has not returned the merge commit', result['reason'])

    def test_merge_auto_never_merges_a_pull_request_that_was_already_merged(self):
        for label, legacy in [('known', NOT_FOUND), ('unknown', FORBIDDEN)]:
            with self.subTest(label):
                fake = self.merged_elsewhere(legacy, [pull(state='MERGED')])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
                self.assertEqual((result['result'], code), ('pending', 11))
                self.assertFalse(result['mutated'])
                self.assertEqual(fake.merges(), [])

    def test_merge_reports_merged_only_with_the_merge_commit_github_returned(self):
        fake = self.merged_elsewhere(NOT_FOUND, [pull(state='MERGED'), pull(state='MERGED', merge_commit=MERGE_SHA)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual((result['result'], code), ('merged', 0))
        self.assertEqual(result['merge_commit'], MERGE_SHA)
        self.assertFalse(result['mutated'])
        self.assertEqual(fake.merges(), [])

    def test_unknown_required_checks_are_incomplete_while_runs_are_pending(self):
        for command in ['status', 'wait']:
            with self.subTest(command):
                self.clock = Clock()
                fake = FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')], [run(1)]]}, api={f'repos/{REPO}/branches/main/protection': FORBIDDEN})
                code, result = self.invoke(fake, command, str(NUMBER), '--sha', SHA)
                self.assertEqual((result['result'], code), ('incomplete', 12))
                self.assertEqual(result['ci']['runs'][0]['status'], 'in_progress')
                self.assertIn('incomplete', result['reason'])
                self.assertIn('Actions runs still pending', result['reason'])
                self.assertEqual(self.clock.sleeps, [])
                self.assert_read_only(fake)

    def test_the_incomplete_reason_says_when_any_run_is_still_pending(self):
        for status in ['in_progress', 'queued']:
            with self.subTest(status):
                runs = [run(1), run(2, status=status, workflow=2, name='Docs')]
                fake = FakeGitHub([pull()], runs={SHA: [runs]}, api={f'repos/{REPO}/branches/main/protection': FORBIDDEN})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertEqual((result['result'], code), ('incomplete', 12))
                self.assertTrue(result['reason'].endswith('incomplete: the required checks could not be read; Actions runs still pending'))

    def test_only_an_incomplete_reason_with_runs_says_runs_are_still_pending(self):
        cases = {
            'known checks': FakeGitHub([pull()], runs={SHA: [[run(1, status='in_progress')]]}),
            'no runs': FakeGitHub([pull()], runs={SHA: [[]]}, api={f'repos/{REPO}/branches/main/protection': FORBIDDEN}),
        }
        for label, fake in cases.items():
            with self.subTest(label):
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertIn(result['result'], ['pending', 'incomplete'])
                self.assertNotIn('still pending', result['reason'])

    def test_a_definite_run_outcome_outranks_unknown_required_checks(self):
        for conclusion, expected in [('failure', 'failed'), ('cancelled', 'cancelled'), ('action_required', 'blocked')]:
            with self.subTest(conclusion):
                runs = [run(1, conclusion=conclusion), run(2, status='in_progress', workflow=2, name='Docs')]
                fake = FakeGitHub([pull()], runs={SHA: [runs]}, api={f'repos/{REPO}/branches/main/protection': FORBIDDEN})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertEqual(result['result'], expected)
                self.assertFalse(result['ci']['complete'])

    def test_readable_legacy_protection_alone_makes_policy_readable(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}': FORBIDDEN, f'repos/{REPO}/rules/branches/main': FORBIDDEN, f'repos/{REPO}/rulesets': FORBIDDEN, f'repos/{REPO}/branches/main/protection': (200, {'required_linear_history': {'enabled': True}})})
        code, result = self.invoke(fake, 'policy')
        self.assertEqual(code, 0)
        self.assertEqual(result['policy']['protection'], 'legacy')
        self.assertFalse(self.err.splitlines()[-1].startswith('error'))

    def test_status_names_a_draft(self):
        fake = FakeGitHub([pull(draft=True)])
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertIn('draft', result['reason'])
        self.assertIn('draft', self.err.splitlines()[-1])

    def test_policy_with_every_read_denied_is_an_error(self):
        for denied in [FORBIDDEN, NOT_FOUND]:
            with self.subTest(denied=denied):
                fake = FakeGitHub([pull()], api={f'repos/{REPO}': denied, f'repos/{REPO}/rules/branches/main': denied, f'repos/{REPO}/rulesets': denied, f'repos/{REPO}/branches/main/protection': denied})
                code, result = self.invoke(fake, 'policy')
                self.assertEqual(code, 3)
                self.assertEqual(result['policy']['protection'], 'unknown')
                self.assertTrue(self.err.splitlines()[-1].startswith('error'))

    def test_merge_refuses_when_the_user_can_bypass_the_ruleset(self):
        for bypass in ['always', 'pull_requests_only', 'exempt']:
            with self.subTest(bypass=bypass):
                fake = FakeGitHub([pull()], api={f'repos/{REPO}/rulesets/77': (200, RULESET_DETAIL | {'current_user_can_bypass': bypass, 'bypass_actors': [{'actor_type': 'RepositoryRole', 'actor_id': 5}]})}, after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'blocked')
                self.assertIn('can bypass', result['reason'])
                self.assertEqual(result['policy']['bypass'], 'possible')
                self.assertEqual(fake.merges(), [])

    def test_merge_refuses_when_the_bypass_state_is_unknown(self):
        for api in [{f'repos/{REPO}/rulesets/77': FORBIDDEN}, {f'repos/{REPO}/rules/branches/main': FORBIDDEN}, {f'repos/{REPO}/rulesets/77': (200, RULESET_DETAIL | {'current_user_can_bypass': None})}]:
            with self.subTest(api=api):
                fake = FakeGitHub([pull()], api=api, after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'blocked')
                self.assertIn('bypass', result['reason'])
                self.assertEqual(result['policy']['bypass'], 'unknown')
                self.assertEqual(fake.merges(), [])

    def test_a_ruleset_nobody_can_bypass_allows_the_merge(self):
        fake = FakeGitHub([pull()], after_merge=[pull(state='MERGED', merge_commit=MERGE_SHA)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
        self.assertEqual(result['result'], 'merged')
        self.assertEqual(result['policy']['bypass'], 'never')

    def test_a_run_awaiting_approval_is_blocked_not_passed(self):
        fake = FakeGitHub([pull()], runs={SHA: [[run(1, conclusion='action_required')]]})
        code, result = self.invoke(fake, 'status', str(NUMBER))
        self.assertEqual(result['result'], 'blocked')

    def test_every_failing_conclusion_is_failed(self):
        for conclusion in ['failure', 'timed_out', 'startup_failure', 'stale']:
            with self.subTest(conclusion=conclusion):
                fake = FakeGitHub([pull()], runs={SHA: [[run(1, conclusion=conclusion)]]})
                code, result = self.invoke(fake, 'status', str(NUMBER))
                self.assertEqual(result['result'], 'failed')

    def test_auto_merge_with_unknown_support_is_blocked(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}': FORBIDDEN}, after_merge=[pull(auto=AUTO)])
        code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge', '--auto')
        self.assertEqual(result['result'], 'blocked')
        self.assertEqual(result['policy']['auto_merge'], 'unknown')
        self.assertEqual(fake.merges(), [])

    def test_a_denied_ruleset_list_is_null_not_empty(self):
        fake = FakeGitHub([pull()], api={f'repos/{REPO}/rulesets': FORBIDDEN})
        code, result = self.invoke(fake, 'policy')
        self.assertIsNone(result['policy']['rulesets'])
        self.assertEqual(result['policy']['access']['rulesets'], 403)


class CommandLineTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / 'bin').mkdir()
        self.log = self.root / 'gh.jsonl'
        (self.root / 'pull.json').write_text(json.dumps({'data': {'repository': {'pullRequest': pull(state='MERGED', merge_commit=MERGE_SHA)}}}))
        gh = self.root / 'bin/gh'
        gh.write_text(f'''#!/usr/bin/env python3
import json, sys
with open({str(self.log)!r}, "a") as log:
    log.write(json.dumps(sys.argv[1:]) + "\\n")
if sys.argv[1:3] == ["api", "graphql"]:
    print(open({str(self.root / 'pull.json')!r}).read())
    sys.exit(0)
if "actions/runs" in " ".join(sys.argv):
    print("HTTP/2.0 200 OK\\r\\n\\r\\n" + json.dumps({{"total_count": 1, "workflow_runs": [{json.dumps(run(1))}]}}))
    sys.exit(0)
print("HTTP/2.0 404 Not Found\\r\\n\\r\\n" + json.dumps({{"message": "Not Found"}}))
sys.exit(1)
''')
        gh.chmod(0o755)
        self.env = {'PATH': str(self.root / 'bin') + os.pathsep + os.environ['PATH'], 'HOME': str(self.root)}

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_status_runs_through_the_real_gh_boundary(self):
        result = subprocess.run(['python3', str(TOOL), '--repo', REPO, 'status', str(NUMBER)], env=self.env, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report['result'], 'merged')
        self.assertEqual(report['merge_commit'], MERGE_SHA)
        self.assertEqual(report['policy']['protection'], 'unknown')
        self.assertTrue(self.calls())
        self.assertFalse(any(call[:2] == ['pr', 'merge'] for call in self.calls()))

    def test_merge_without_opt_in_never_calls_gh(self):
        result = subprocess.run(['python3', str(TOOL), '--repo', REPO, 'merge', str(NUMBER), '--sha', SHA], env=self.env, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 2)
        self.assertIn('--confirm-merge', result.stderr)
        self.assertEqual(self.calls(), [])


if __name__ == '__main__':
    unittest.main()
