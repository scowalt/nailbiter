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
EXIT_CODES = {'passed': 0, 'merged': 0, 'queued': 10, 'pending': 11, 'error': 3, 'failed': 20, 'cancelled': 21, 'blocked': 22, 'superseded': 23, 'timeout': 24}
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


def run(run_id, status='completed', conclusion='success', workflow=1, name='CI', sha=SHA, created='2026-11-12T10:00:00Z'):
    return {'id': run_id, 'name': name, 'workflow_id': workflow, 'event': 'pull_request', 'status': status, 'conclusion': conclusion if status == 'completed' else None, 'head_sha': sha, 'run_attempt': 1, 'run_number': run_id, 'created_at': created, 'html_url': f'https://github.com/{REPO}/actions/runs/{run_id}'}


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
    def __init__(self, pulls, runs=None, api=None, merge=(0, ''), after_merge=None, graphql_denied=False):
        self.pulls = list(pulls)
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
            return Process(0, json.dumps({'data': {'repository': {'pullRequest': self.next(self.pulls)}}}), '')
        if argv[0] == 'api':
            target = urlsplit(next(argument for argument in argv[1:] if argument.startswith('repos/')))
            if target.path == f'repos/{REPO}/actions/runs':
                if 'runs' in self.api:
                    return http(*self.api['runs'])
                sha = parse_qs(target.query)['head_sha'][0]
                runs = self.next(self.runs[sha]) if sha in self.runs else []
                return http(200, {'total_count': len(runs), 'workflow_runs': runs})
            return http(*self.api.get(target.path, NOT_FOUND))
        raise AssertionError(f'unexpected gh invocation {argv}')

    def merges(self):
        return [call for call in self.calls if call[:2] == ['pr', 'merge']]

    def mutating_calls(self):
        return [call for call in self.calls if call[:2] == ['pr', 'merge'] or (call[0] == 'api' and any(flag in call for flag in ['-X', '--method'])) or any('mutation' in argument for argument in call)]


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
                self.assertEqual(result['result'], 'passed')

    def test_denied_pull_request_or_actions_access_is_an_error(self):
        for fake in [FakeGitHub([pull()], graphql_denied=True), FakeGitHub([pull()], api={'runs': FORBIDDEN})]:
            with self.subTest(calls=fake.api.get('runs')):
                code, result = self.invoke(fake, 'merge', str(NUMBER), '--sha', SHA, '--confirm-merge')
                self.assertEqual(result['result'], 'error')
                self.assertIn('not accessible', result['reason'])
                self.assertEqual(fake.merges(), [])


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
