#!/usr/bin/env python3
from dataclasses import dataclass
import json
import os
import re
import subprocess
import sys
import urllib.request

OSV_QUERY = 'https://api.osv.dev/v1/query'
GITHUB_API = 'https://api.github.com'
PAGE_SIZE = 100
HTTP_TIMEOUT_SECONDS = 60
VERSION = re.compile(r'v?([0-9]+)\.([0-9]+)\.([0-9]+)')
COMPARATOR = re.compile(r'\s*(>=|<=|>|<|=)?\s*(\S+)\s*')
COMPARISONS = {
    '>=': lambda a, b: a >= b, '<=': lambda a, b: a <= b, '>': lambda a, b: a > b,
    '<': lambda a, b: a < b, '=': lambda a, b: a == b,
}


@dataclass(frozen=True)
class Tool:
    name: str
    version_command: tuple
    osv_package: dict
    repository: str
    ecosystem: str
    package: str


@dataclass(frozen=True)
class Advisory:
    id: str
    aliases: frozenset
    summary: str
    url: str
    source: str


TOOLS = (
    Tool('bun', ('bun', '--version'), {'ecosystem': 'npm', 'name': 'bun'}, 'oven-sh/bun', 'npm', 'bun'),
    Tool('trivy', ('trivy', '--version'), {'ecosystem': 'Go', 'name': 'github.com/aquasecurity/trivy'},
         'aquasecurity/trivy', 'go', 'github.com/aquasecurity/trivy'),
)


def parse_version(text):
    match = VERSION.fullmatch(text.strip())
    if match is None:
        raise ValueError(f'not a MAJOR.MINOR.PATCH version: {text!r}')
    return tuple(int(part) for part in match.groups())


def installed_version(tool, run=subprocess.run):
    output = run(list(tool.version_command), capture_output=True, text=True, check=True).stdout
    match = VERSION.search(output)
    if match is None:
        raise ValueError(f'{" ".join(tool.version_command)} printed no version: {output!r}')
    return '.'.join(match.groups())


def in_range(version, vulnerable_range):
    pinned = parse_version(version)
    for clause in vulnerable_range.split(','):
        match = COMPARATOR.fullmatch(clause)
        if match is None:
            raise ValueError(f'unreadable version range: {vulnerable_range!r}')
        operator, bound = match.group(1) or '=', parse_version(match.group(2))
        if not COMPARISONS[operator](pinned, bound):
            return False
    return True


def repository_advisories(tool, version, advisories):
    found = []
    for advisory in advisories:
        if advisory.get('withdrawn_at'):
            continue
        ranges = [vulnerability.get('vulnerable_version_range') or ''
                  for vulnerability in advisory.get('vulnerabilities') or []
                  if (vulnerability.get('package') or {}).get('ecosystem', '').lower() == tool.ecosystem
                  and (vulnerability.get('package') or {}).get('name') == tool.package]
        notes = []
        for vulnerable_range in ranges:
            try:
                if in_range(version, vulnerable_range):
                    notes.append(f'affects {vulnerable_range}')
            except ValueError as error:
                notes.append(f'check by hand, {error}')
        if notes:
            found.append(Advisory(advisory['ghsa_id'], frozenset([advisory.get('cve_id')]) - {None},
                                  f'{advisory.get("summary", "")} ({"; ".join(notes)})',
                                  advisory.get('html_url', ''), f'{tool.repository} security advisories'))
    return found


def osv_advisories(response):
    return [Advisory(vulnerability['id'], frozenset(vulnerability.get('aliases') or []),
                     vulnerability.get('summary', ''), f'https://osv.dev/vulnerability/{vulnerability["id"]}', 'OSV')
            for vulnerability in response.get('vulns') or []]


def merge(advisories):
    merged, known = [], set()
    for advisory in advisories:
        names = {advisory.id} | advisory.aliases
        if names & known:
            continue
        known |= names
        merged.append(advisory)
    return merged


def fetch_json(url, body=None):
    headers = {'Accept': 'application/json', 'User-Agent': 'nailbiter-tool-advisories'}
    token = os.environ.get('GH_TOKEN') if url.startswith(GITHUB_API) else None
    if token:
        headers['Authorization'] = f'Bearer {token}'
    data = None if body is None else json.dumps(body).encode()
    if data is not None:
        headers['Content-Type'] = 'application/json'
    with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers), timeout=HTTP_TIMEOUT_SECONDS) as response:
        return json.load(response)


def published_repository_advisories(repository, fetch):
    advisories, page = [], 1
    while True:
        batch = fetch(f'{GITHUB_API}/repos/{repository}/security-advisories?state=published&per_page={PAGE_SIZE}&page={page}')
        advisories.extend(batch)
        if len(batch) < PAGE_SIZE:
            return advisories
        page += 1


def advisories_for(tool, version, fetch):
    osv = osv_advisories(fetch(OSV_QUERY, {'package': tool.osv_package, 'version': version}))
    repository = repository_advisories(tool, version, published_repository_advisories(tool.repository, fetch))
    return merge(osv + repository)


def issue_title(tool, version):
    return f'Published advisories affect the pinned CI {tool.name} {version}'


def advisory_lines(advisories):
    return ''.join(f'- [{advisory.id}]({advisory.url}): {advisory.summary} (from {advisory.source})\n' for advisory in advisories)


def issue_body(tool, version, advisories):
    return (f'The scheduled `Tool advisories` workflow found advisories that affect {tool.name} {version}, '
            f'the version that CI installs with `scripts/ci/install-{tool.name}.sh`.\n\n'
            f'{advisory_lines(advisories)}\n'
            'Move the pin to a fixed release as `docs/deployment.md` describes under "CI Bun and Trivy", '
            f'and add each advisory to the `tests/ci/test_tool_caches.py` table for {tool.name}.\n')


def gh_json(run, *args):
    return json.loads(run(['gh', *args], capture_output=True, text=True, check=True).stdout)


def report(tool, version, advisories, repository, run=subprocess.run):
    title = issue_title(tool, version)
    issues = gh_json(run, 'issue', 'list', '--repo', repository, '--state', 'open', '--search', f'"{title}" in:title',
                     '--json', 'number,title,body,comments', '--limit', '100')
    existing = next((issue for issue in issues if issue['title'] == title), None)
    if existing is None:
        run(['gh', 'issue', 'create', '--repo', repository, '--title', title, '--body', issue_body(tool, version, advisories)],
            check=True)
        return f'opened an issue: {title}'
    seen = existing['body'] + ''.join(comment['body'] for comment in existing.get('comments') or [])
    new = [advisory for advisory in advisories if advisory.id not in seen]
    if not new:
        return f'issue #{existing["number"]} already lists every advisory'
    run(['gh', 'issue', 'comment', str(existing['number']), '--repo', repository,
         '--body', f'More advisories affect {tool.name} {version}:\n\n{advisory_lines(new)}'], check=True)
    return f'commented on issue #{existing["number"]} with {len(new)} new advisories'


def main(repository, fetch=fetch_json, run=subprocess.run, tools=TOOLS):
    for tool in tools:
        version = installed_version(tool, run)
        advisories = advisories_for(tool, version, fetch)
        print(f'{tool.name} {version}: {len(advisories)} advisories')
        print(advisory_lines(advisories), end='')
        if advisories:
            print(report(tool, version, advisories, repository, run))
    return 0


if __name__ == '__main__':
    sys.exit(main(os.environ['GITHUB_REPOSITORY']))
