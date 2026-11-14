#!/usr/bin/env python3
import argparse
from dataclasses import dataclass
import enum
import json
import os
import re
import subprocess
import sys
import time
from urllib.parse import quote

DEFAULT_REPO = 'scowalt/nailbiter'
FIRST_POLL_SECONDS = 15
MAX_POLL_SECONDS = 120
DEFAULT_TIMEOUT_SECONDS = 3600
CONFIRM_TIMEOUT_SECONDS = 120
FIRST_CONFIRM_POLL_SECONDS = 5
MAX_CONFIRM_POLL_SECONDS = 30
GH_TIMEOUT_SECONDS = 120
MERGE_METHOD_SETTINGS = {'squash': 'allow_squash_merge', 'merge': 'allow_merge_commit', 'rebase': 'allow_rebase_merge'}
PASSING_CONCLUSIONS = {'success', 'skipped', 'neutral'}
ACTIONS_ONLY_NOTE = (
    'GitHub Actions runs and jobs only; third-party statuses, reviews and checks Actions does not produce '
    'are enforced by GitHub at merge time and are not verified here'
)
OPTIONAL = type(None)
RUN_FIELDS = {
    'id': int, 'name': str, 'workflow_id': int, 'event': str, 'status': str,
    'conclusion': (str, OPTIONAL), 'head_sha': str, 'created_at': str,
}
JOB_FIELDS = {'name': str, 'status': str, 'conclusion': (str, OPTIONAL)}
PULL_FIELDS = {
    'headRefOid': str, 'state': str, 'isDraft': bool, 'baseRefName': str, 'mergeable': str,
    'mergeCommit': (dict, OPTIONAL), 'autoMergeRequest': (dict, OPTIONAL), 'mergeQueueEntry': (dict, OPTIONAL),
}
NESTED_PULL_FIELDS = {'mergeCommit': {'oid': str}, 'autoMergeRequest': {'mergeMethod': str}, 'mergeQueueEntry': {'state': str}}
LEGACY_MEANINGS = {
    200: 'legacy branch protection is configured',
    403: 'not visible to this token',
    404: 'not found or not visible to this token; a 404 is not proof that the branch has no protection',
}
PULL_QUERY = '''query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      number url state isDraft headRefOid baseRefName mergeable mergeStateStatus
      mergeCommit { oid }
      autoMergeRequest { enabledAt mergeMethod }
      mergeQueueEntry { state position }
    }
  }
}'''


class Result(enum.StrEnum):
    PASSED = 'passed'
    INCOMPLETE = 'incomplete'
    MERGED = 'merged'
    ERROR = 'error'
    QUEUED = 'queued'
    PENDING = 'pending'
    FAILED = 'failed'
    CANCELLED = 'cancelled'
    BLOCKED = 'blocked'
    SUPERSEDED = 'superseded'
    TIMEOUT = 'timeout'


EXIT_CODES = {
    Result.PASSED: 0, Result.MERGED: 0, Result.ERROR: 3, Result.QUEUED: 10, Result.PENDING: 11, Result.INCOMPLETE: 12,
    Result.FAILED: 20, Result.CANCELLED: 21, Result.BLOCKED: 22, Result.SUPERSEDED: 23, Result.TIMEOUT: 24,
}
CI_PRECEDENCE = (Result.FAILED, Result.CANCELLED, Result.BLOCKED, Result.INCOMPLETE, Result.PENDING, Result.PASSED)


class GitHubError(Exception):
    pass


@dataclass(frozen=True)
class Response:
    status: int | None
    body: object
    error: str


def shaped(record, fields, what):
    if not isinstance(record, dict) or any(key not in record or not isinstance(record[key], kind) for key, kind in fields.items()):
        raise GitHubError(f'{what} had an unexpected shape')
    return record


def parse_pull(pull, what):
    shaped(pull, PULL_FIELDS, what)
    for key, fields in NESTED_PULL_FIELDS.items():
        if pull.get(key) is not None:
            shaped(pull[key], fields, f'{what} {key}')
    return pull


def list_of_dicts(response):
    body = response.body
    return body if response.status == 200 and isinstance(body, list) and all(isinstance(item, dict) for item in body) else None


def first_line(text):
    return next((line.strip() for line in text.splitlines() if line.strip()), '')


def run_gh(argv):
    try:
        env = os.environ | {'GH_PROMPT_DISABLED': '1', 'NO_COLOR': '1'}
        return subprocess.run(['gh', *argv], capture_output=True, text=True, timeout=GH_TIMEOUT_SECONDS, env=env)
    except subprocess.TimeoutExpired:
        return subprocess.CompletedProcess(argv, 124, '', f'gh did not answer within {GH_TIMEOUT_SECONDS}s')
    except FileNotFoundError:
        return subprocess.CompletedProcess(argv, 127, '', 'gh is not installed')


class GitHub:
    def __init__(self, repo, run):
        self.repo = repo
        self.run = run

    def get(self, path):
        process = self.run(['api', '--include', f'repos/{self.repo}{path}'])
        head, _, body = process.stdout.replace('\r\n', '\n').partition('\n\n')
        status = re.match(r'HTTP/[\d.]+ (\d{3})', head)
        try:
            payload = json.loads(body) if body.strip() else None
        except ValueError:
            payload = None
        message = payload.get('message') if isinstance(payload, dict) else None
        return Response(int(status.group(1)) if status else None, payload, message or first_line(process.stderr) or 'gh api failed')

    def require(self, path, key):
        response = self.get(path)
        if response.status != 200:
            raise GitHubError(f'GET {path} returned HTTP {response.status}: {response.error}')
        if not isinstance(response.body, dict) or not isinstance(response.body.get(key), list):
            raise GitHubError(f'GET {path} returned malformed JSON without a {key} list')
        return response.body[key]

    def pull_request(self, number):
        owner, name = self.repo.split('/')
        process = self.run(['api', 'graphql', '-f', f'owner={owner}', '-f', f'name={name}', '-F', f'number={number}', '-f', f'query={PULL_QUERY}'])
        if process.returncode != 0:
            raise GitHubError(f'reading pull request #{number} failed: {first_line(process.stderr) or first_line(process.stdout)}')
        try:
            document = json.loads(process.stdout)
        except ValueError:
            raise GitHubError(f'reading pull request #{number} returned malformed JSON') from None
        pull = (((document if isinstance(document, dict) else {}).get('data') or {}).get('repository') or {}).get('pullRequest')
        if not pull:
            raise GitHubError(f'pull request #{number} was not found in {self.repo}')
        return parse_pull(pull, f'pull request #{number}')

    def runs(self, sha):
        runs = self.require(f'/actions/runs?head_sha={sha}&per_page=100', 'workflow_runs')
        return [shaped(run, RUN_FIELDS, 'an Actions run') for run in runs]

    def jobs(self, run_id):
        jobs = self.require(f'/actions/runs/{run_id}/jobs?per_page=100', 'jobs')
        return [shaped(job, JOB_FIELDS, 'an Actions job') for job in jobs]

    def merge(self, number, sha, method, auto):
        auto_flag = ['--auto'] if auto else []
        process = self.run(['pr', 'merge', str(number), '--repo', self.repo, *auto_flag, f'--{method}', '--match-head-commit', sha])
        if process.returncode == 0:
            return None
        return first_line(process.stderr) or first_line(process.stdout) or f'gh pr merge exited {process.returncode}'


@dataclass(frozen=True)
class Ci:
    state: Result
    runs: tuple
    superseded: tuple
    required: dict | None

    def summary(self, sha):
        parts = [
            'Actions-only evidence: ' + '; '.join(f"{run['name']} {run['event']} {run['conclusion'] or run['status']}" for run in self.runs)
            if self.runs else f'no Actions runs yet for {sha[:12]}'
        ]
        runs_passed = self.runs and all(run_outcome(run) is Result.PASSED for run in self.runs)
        missing = [name for name, jobs in (self.required or {}).items() if not jobs]
        if runs_passed and missing:
            parts.append(f"no Actions job yet for required checks: {', '.join(missing)}")
        unsuccessful = [
            f"{name} {job['conclusion'] or 'not finished'}"
            for name, jobs in (self.required or {}).items() for job in jobs
            if job['conclusion'] not in PASSING_CONCLUSIONS
        ]
        if unsuccessful:
            parts.append(f"required check jobs: {', '.join(unsuccessful)}")
        if self.state is Result.INCOMPLETE:
            parts.append('incomplete: the required checks could not be read')
        if self.state is Result.PASSED:
            parts.append('third-party checks and reviews are enforced by GitHub at merge time')
        return '; '.join(parts)

    def to_json(self):
        fields = ['id', 'name', 'workflow_id', 'event', 'status', 'conclusion', 'run_attempt', 'html_url']
        return {
            'evidence': 'actions-only',
            'state': self.state.value,
            'complete': self.required is not None,
            'note': ACTIONS_ONLY_NOTE,
            'required_checks': self.required,
            'runs': [{key: run.get(key) for key in fields} for run in self.runs],
            'superseded_runs': list(self.superseded),
        }


def run_outcome(run):
    if run['status'] != 'completed':
        return Result.PENDING
    if run['conclusion'] in PASSING_CONCLUSIONS:
        return Result.PASSED
    return {'cancelled': Result.CANCELLED, 'action_required': Result.BLOCKED}.get(run['conclusion'], Result.FAILED)


def classify_ci(runs, sha, required_checks, read_jobs):
    own = sorted((run for run in runs if run['head_sha'] == sha), key=lambda run: (run['created_at'], run['id']))
    latest = {}
    for run in own:
        latest[(run['workflow_id'], run['event'])] = run
    current = tuple(latest.values())
    outcomes = {run_outcome(run) for run in current} or {Result.PENDING}
    required = None if required_checks is None else {name: [] for name in required_checks}
    if required and outcomes == {Result.PASSED}:
        jobs = [(run['id'], job) for run in current for job in read_jobs(run['id']) if job['name'] in required]
        outcomes = {run_outcome(job) for _, job in jobs}
        for run_id, job in jobs:
            required[job['name']].append({'run_id': run_id, 'conclusion': job['conclusion']})
        if not all(required.values()):
            outcomes.add(Result.PENDING)
    if required is None:
        outcomes.add(Result.INCOMPLETE)
    state = next(result for result in CI_PRECEDENCE if result in outcomes)
    return Ci(state, current, tuple(sorted(run['id'] for run in own if run not in current)), required)


def classify_pull(pull, sha):
    head = pull['headRefOid']
    if head != sha:
        return Result.SUPERSEDED, f'the head moved from {sha[:12]} to {head[:12]}; this revision will not be merged'
    if pull['state'] == 'MERGED':
        commit = (pull['mergeCommit'] or {}).get('oid')
        if not commit:
            return Result.PENDING, 'GitHub reports merged but has not returned the merge commit yet'
        return Result.MERGED, f'merged as {commit[:12]}'
    if pull['state'] != 'OPEN':
        return Result.BLOCKED, f"the pull request is {pull['state'].lower()}"
    if pull['autoMergeRequest']:
        return Result.QUEUED, f"auto-merge ({pull['autoMergeRequest']['mergeMethod'].lower()}) is enabled; GitHub merges when its policy is met"
    if pull['mergeQueueEntry']:
        return Result.QUEUED, f"in the merge queue ({pull['mergeQueueEntry']['state'].lower()})"
    return None


def classify(pull, sha, ci):
    return classify_pull(pull, sha) or (ci.state, ci.summary(sha) + ('; the pull request is a draft' if pull['isDraft'] else ''))


def merge_blocker(pull):
    if pull['isDraft']:
        return 'the pull request is a draft'
    if pull['mergeable'] == 'CONFLICTING':
        return 'the pull request has merge conflicts'
    return None


def bypass_state(values):
    if values - {'never', 'unknown'}:
        return 'possible'
    return 'unknown' if 'unknown' in values else 'never'


def ruleset_contexts(applied):
    contexts = []
    for rule in applied:
        if rule.get('type') != 'required_status_checks':
            continue
        for check in (rule.get('parameters') or {}).get('required_status_checks', []):
            context = check.get('context') if isinstance(check, dict) else None
            if isinstance(context, str):
                contexts.append(context)
    return contexts


def legacy_contexts(legacy):
    if legacy.status == 404:
        return []
    if legacy.status != 200 or not isinstance(legacy.body, dict):
        return None
    checks = legacy.body.get('required_status_checks')
    if checks is None:
        return []
    if not isinstance(checks, dict):
        return None
    contexts = checks.get('contexts', [])
    entries = checks.get('checks', [])
    if not isinstance(contexts, list) or not all(isinstance(context, str) for context in contexts):
        return None
    if not isinstance(entries, list) or not all(isinstance(entry, dict) and isinstance(entry.get('context'), str) for entry in entries):
        return None
    return contexts + [entry['context'] for entry in entries]


def required_contexts(applied, legacy):
    legacy_required = legacy_contexts(legacy)
    if applied is None or legacy_required is None:
        return None
    return list(dict.fromkeys(ruleset_contexts(applied) + legacy_required))


def describe_ruleset(entry, known, applied_ids, applied):
    return {
        'id': entry['id'],
        'name': entry.get('name'),
        'source': entry.get('source'),
        'enforcement': entry.get('enforcement'),
        'applies_to_branch': entry['id'] in applied_ids if applied is not None else None,
        'bypass_actors': len(known['bypass_actors']) if 'bypass_actors' in known else None,
        'current_user_can_bypass': known.get('current_user_can_bypass') or 'unknown',
    }


def discover_policy(gh, branch):
    encoded = quote(branch, safe='')
    settings = gh.get('')
    rules = gh.get(f'/rules/branches/{encoded}')
    listed = gh.get('/rulesets?includes_parents=true&per_page=100')
    legacy = gh.get(f'/branches/{encoded}/protection')
    applied = list_of_dicts(rules)
    applied_ids = sorted({rule['ruleset_id'] for rule in applied or [] if isinstance(rule.get('ruleset_id'), int)})
    details = {ruleset_id: gh.get(f'/rulesets/{ruleset_id}?includes_parents=true') for ruleset_id in applied_ids}
    known_details = {
        ruleset_id: detail.body if detail.status == 200 and isinstance(detail.body, dict) else {}
        for ruleset_id, detail in details.items()
    }
    bypasses = {
        known.get('current_user_can_bypass') if isinstance(known.get('current_user_can_bypass'), str) else 'unknown'
        for known in known_details.values()
    }
    bypass = 'unknown' if applied is None else bypass_state(bypasses)
    entries = list_of_dicts(listed)
    rulesets = None
    if entries is not None and all(isinstance(entry.get('id'), int) for entry in entries):
        rulesets = [describe_ruleset(entry, known_details.get(entry['id'], {}), applied_ids, applied) for entry in entries]
    readable_settings = settings.body if settings.status == 200 and isinstance(settings.body, dict) else None
    auto = readable_settings.get('allow_auto_merge') if readable_settings else None
    protection = '+'.join(source for source, present in [('rulesets', bool(applied)), ('legacy', legacy.status == 200)] if present) or 'unknown'
    return {
        'branch': branch,
        'protection': protection,
        'rules': [rule.get('type') for rule in applied] if applied is not None else None,
        'required_checks': required_contexts(applied, legacy),
        'rulesets': rulesets,
        'legacy_protection': {'http_status': legacy.status, 'meaning': LEGACY_MEANINGS.get(legacy.status, legacy.error)},
        'merge_methods': {method: readable_settings.get(setting) for method, setting in MERGE_METHOD_SETTINGS.items()} if readable_settings else None,
        'auto_merge': {True: 'enabled', False: 'disabled'}.get(auto, 'unknown'),
        'bypass': bypass,
        'readable': readable_settings is not None or applied is not None or rulesets is not None or legacy.status == 200,
        'access': {
            'settings': settings.status,
            'rules': rules.status,
            'rulesets': listed.status,
            'ruleset_details': {str(ruleset_id): detail.status for ruleset_id, detail in details.items()},
            'legacy_protection': legacy.status,
        },
    }


def describe_policy(policy):
    methods = policy['merge_methods']
    allowed = ', '.join(method for method, enabled in methods.items() if enabled) if methods else 'unknown'
    checks = 'unknown' if policy['required_checks'] is None else ', '.join(policy['required_checks']) or 'none'
    prefix = '' if policy['readable'] else 'error: no policy endpoint was readable; '
    return (
        f"{prefix}policy for {policy['branch']}: protection {policy['protection']}; required checks: {checks}; "
        f"ruleset bypass {policy['bypass']}; merge methods: {allowed}; auto-merge {policy['auto_merge']}; "
        f"legacy protection endpoint HTTP {policy['legacy_protection']['http_status']}"
    )


class Session:
    def __init__(self, gh, args, clock, sleep, err):
        self.gh = gh
        self.args = args
        self.clock = clock
        self.sleep = sleep
        self.err = err
        self.expected_sha = getattr(args, 'sha', None)
        self.pull = None
        self.ci = None
        self.policy = None
        self.mutated = False
        self.last_progress = None

    def observe(self, refresh_ci=True):
        self.pull = self.gh.pull_request(self.args.number)
        self.expected_sha = self.expected_sha or self.pull['headRefOid']
        if self.policy is None:
            self.policy = discover_policy(self.gh, self.pull['baseRefName'])
        if refresh_ci or self.ci is None:
            self.ci = classify_ci(self.gh.runs(self.expected_sha), self.expected_sha, self.policy['required_checks'], self.gh.jobs)
        return classify(self.pull, self.expected_sha, self.ci)

    def progress(self, result, reason):
        if (result, reason) != self.last_progress:
            self.last_progress = (result, reason)
            print(f'{result}: {reason}', file=self.err, flush=True)

    def wait(self, timeout, observation):
        deadline = self.clock() + timeout
        interval = FIRST_POLL_SECONDS
        while observation[0] is Result.PENDING:
            remaining = deadline - self.clock()
            if remaining <= 0:
                return Result.TIMEOUT, f'still pending after {timeout}s: {observation[1]}'
            self.progress(*observation)
            self.sleep(min(interval, remaining))
            interval = min(interval * 3 // 2, MAX_POLL_SECONDS)
            observation = self.observe()
        return observation

    def confirm(self, rejection):
        deadline = self.clock() + CONFIRM_TIMEOUT_SECONDS
        interval = FIRST_CONFIRM_POLL_SECONDS
        while True:
            result, reason = self.observe(refresh_ci=False)
            if result in (Result.MERGED, Result.QUEUED, Result.SUPERSEDED) or self.pull['state'] == 'CLOSED':
                return result, reason
            if rejection:
                return Result.BLOCKED, f'GitHub rejected the merge: {rejection}'
            remaining = deadline - self.clock()
            if remaining <= 0:
                return Result.TIMEOUT, f'gh pr merge returned but GitHub did not report the pull request merged or queued within {CONFIRM_TIMEOUT_SECONDS}s'
            self.sleep(min(interval, remaining))
            interval = min(interval * 2, MAX_CONFIRM_POLL_SECONDS)


def status_command(session):
    return session.observe()


def wait_command(session):
    return session.wait(session.args.timeout, session.observe())


def merge_refusal(session):
    args = session.args
    if (session.policy['merge_methods'] or {}).get(args.method) is False:
        return f'the repository does not allow {args.method} merges'
    if args.auto and session.policy['auto_merge'] != 'enabled':
        return (
            f"native auto-merge is {session.policy['auto_merge']} for {args.repo}; nothing was queued. "
            'Rerun without --auto to wait for eligibility and merge'
        )
    if session.policy['bypass'] == 'possible':
        return f"the current user can bypass a ruleset on {session.policy['branch']}; this tool does not merge where a bypass could skip the rules"
    if session.policy['bypass'] != 'never':
        return f"the ruleset bypass state for {session.policy['branch']} could not be read; this tool does not merge unless it can confirm no bypass applies"
    return merge_blocker(session.pull)


def merge_command(session):
    args = session.args
    result, reason = status_command(session)
    if result not in (Result.PENDING, Result.PASSED, Result.INCOMPLETE):
        return result, reason
    refusal = merge_refusal(session)
    if refusal:
        return Result.BLOCKED, refusal
    if not args.auto:
        result, reason = session.wait(args.timeout, (result, reason))
        if result is not Result.PASSED:
            return result, reason
    result, reason = session.observe(refresh_ci=False)
    if result not in (Result.PENDING, Result.PASSED):
        return result, reason
    refusal = merge_blocker(session.pull)
    if refusal:
        return Result.BLOCKED, refusal
    session.mutated = True
    rejection = session.gh.merge(args.number, session.expected_sha, args.method, args.auto)
    try:
        return session.confirm(rejection)
    except GitHubError as error:
        return Result.ERROR, (
            f'gh pr merge was called but reading the result failed ({error}); the merge outcome is unknown. '
            'Read the pull request before running merge again'
        )


def pull_summary(pull):
    if pull is None:
        return None
    return {
        'url': pull.get('url'),
        'state': pull.get('state'),
        'is_draft': pull.get('isDraft'),
        'mergeable': pull.get('mergeable'),
        'merge_state_status': pull.get('mergeStateStatus'),
        'auto_merge': pull.get('autoMergeRequest'),
        'merge_queue': pull.get('mergeQueueEntry'),
    }


def write_result(session, result, reason, out, err):
    pull = session.pull
    document = {
        'result': result.value,
        'reason': reason,
        'repo': session.args.repo,
        'number': session.args.number,
        'expected_sha': session.expected_sha,
        'head_sha': pull and pull['headRefOid'],
        'mutated': session.mutated,
    }
    if result is Result.MERGED:
        document['merge_commit'] = pull['mergeCommit']['oid']
    document |= {'pull_request': pull_summary(pull), 'ci': session.ci and session.ci.to_json(), 'policy': session.policy}
    print(json.dumps(document, indent=2), file=out)
    revision = session.expected_sha[:12] if session.expected_sha else 'unknown head'
    suffix = '' if session.mutated else ' (no changes made)'
    print(f'{result}: {session.args.repo}#{session.args.number} @ {revision}: {reason}{suffix}', file=err)
    return EXIT_CODES[result]


def full_sha(value):
    if not re.fullmatch(r'[0-9a-f]{40}', value):
        raise argparse.ArgumentTypeError('expected a full 40-character lowercase commit SHA')
    return value


def positive(value):
    number = int(value)
    if number <= 0:
        raise argparse.ArgumentTypeError('expected a positive integer')
    return number


def repository(value):
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', value):
        raise argparse.ArgumentTypeError('expected OWNER/NAME')
    return value


def parse_args(argv):
    parser = argparse.ArgumentParser(prog='pr-ci', description=(
        'Inspect GitHub Actions for one pull request revision, wait for it, or merge it when explicitly authorized. '
        'Prints JSON on stdout and progress on stderr.'
    ))
    parser.add_argument('--repo', type=repository, default=DEFAULT_REPO, help='OWNER/NAME (default: %(default)s)')
    commands = parser.add_subparsers(dest='command', required=True)
    policy = commands.add_parser('policy', help='Report rulesets, legacy protection, merge methods and auto-merge for a branch.')
    policy.add_argument('--branch', default='main')
    helps = {
        'status': 'Report the current state once. Read-only.',
        'wait': 'Poll until the revision leaves the pending state or the timeout expires. Read-only.',
        'merge': 'Wait for Actions, then merge the expected head revision. Requires --confirm-merge.',
    }
    for name, help_text in helps.items():
        command = commands.add_parser(name, help=help_text)
        command.add_argument('number', type=positive)
        command.add_argument('--sha', type=full_sha, required=name == 'merge', help='Pin to this head SHA; defaults to the current head for status and wait.')
        if name != 'status':
            command.add_argument('--timeout', type=positive, default=DEFAULT_TIMEOUT_SECONDS, help='Seconds to wait for Actions (default: %(default)s).')
    merge = commands.choices['merge']
    merge.add_argument('--confirm-merge', action='store_true', help=(
        'Required. Merging to main starts a production release; pass this only when the merge is authorized.'
    ))
    merge.add_argument('--method', choices=sorted(MERGE_METHOD_SETTINGS), default='squash')
    merge.add_argument('--auto', action='store_true', help=(
        'Queue native auto-merge instead of waiting; reports blocked when the repository disables auto-merge.'
    ))
    args = parser.parse_args(argv)
    if args.command == 'merge' and not args.confirm_merge:
        parser.error('merge needs --confirm-merge and --sha <expected head>; creating a pull request is not authorization to merge it')
    return args


def main(argv=None, run=run_gh, clock=time.monotonic, sleep=time.sleep, out=sys.stdout, err=sys.stderr):
    args = parse_args(argv)
    gh = GitHub(args.repo, run)
    if args.command == 'policy':
        policy = discover_policy(gh, args.branch)
        print(json.dumps({'repo': args.repo, 'policy': policy}, indent=2), file=out)
        print(describe_policy(policy), file=err)
        return 0 if policy['readable'] else EXIT_CODES[Result.ERROR]
    session = Session(gh, args, clock, sleep, err)
    command = {'status': status_command, 'wait': wait_command, 'merge': merge_command}[args.command]
    try:
        result, reason = command(session)
    except GitHubError as error:
        result, reason = Result.ERROR, str(error)
    return write_result(session, result, reason, out, err)


if __name__ == '__main__':
    sys.exit(main())
