# Pull request CI status, waiting and merging

Every merge to `main` starts a production release. Creating a pull request is not permission to merge it. Merge only when the current task explicitly authorizes that merge, and pass the head revision that was reviewed.

`scripts/pr/pr-ci.py` inspects one pull request revision through the existing `gh` login. It needs Python 3.14 and `gh`, and nothing else. It prints a JSON report on stdout. On stderr it prints a progress line each time the state changes, then one summary line.

## Commands

```sh
python3 scripts/pr/pr-ci.py policy
python3 scripts/pr/pr-ci.py status 147
python3 scripts/pr/pr-ci.py wait 145 --sha <head-sha> --timeout 3600
python3 scripts/pr/pr-ci.py merge 145 --sha <head-sha> --confirm-merge
```

- `policy` reports the rulesets that apply to a branch (default `main`), their required checks, whether the current user can bypass them, the legacy branch-protection endpoint, the merge methods the repository allows and whether native auto-merge is enabled. It exits 3 when none of these reads succeed.
- `status` reads the pull request, the branch policy and the GitHub Actions runs for one head SHA once. Without `--sha` it pins to the current head. The reason names a draft pull request.
- `wait` polls until the revision leaves `pending` or the timeout expires. The interval starts at 15 seconds, grows by half each poll, is capped at 120 seconds and stops at the deadline. Each `gh` call has its own 120-second limit, so one slow call can overrun the deadline by up to 120 seconds. The tool reads the branch policy once per invocation, so `wait` returns `incomplete` at once when the required checks cannot be read; waiting cannot make them known.
- `merge` needs `--confirm-merge` and the full expected head SHA. It waits for Actions like `wait`. Immediately before merging, it reads the pull request again. If the head, state, auto-merge request or merge queue entry changed, it stops. Then it runs `gh pr merge <N> --squash --match-head-commit <sha>` and reads the pull request back. It never passes `--admin` or `--delete-branch`.
- `merge` refuses with `blocked` when the current user can bypass a ruleset that applies to the base branch (`current_user_can_bypass` is anything but `never`). It also refuses when that state cannot be read, because it cannot then show that the merge does not rely on a bypass. When the state is unknown, use a token that can read the ruleset.
- `merge --auto` queues native auto-merge with the same `--match-head-commit <sha>` instead of waiting. When the pull request can already be merged (`isImmediatelyMergeable`), `gh pr merge --auto` merges it at once instead of queueing it. When the repository disables auto-merge, or its setting cannot be read, the tool reports `blocked` and changes nothing. It does not turn the setting on. It never queues auto-merge while the required checks are unknown, even when the runs are still pending.

`status`, `wait` and `policy` only read. The report's `mutated` field is `true` only after `merge` has run `gh pr merge`.

## Results

| Result | Exit | Meaning |
| --- | --- | --- |
| `passed` | 0 | Every latest Actions run for the SHA succeeded, and each required check has an Actions job of that exact name whose conclusion is success, skipped or neutral. This is Actions-only evidence; see below. |
| `incomplete` | 12 | The required checks could not be read, so the tool cannot tell whether they ran, and no latest run has failed, been cancelled or is awaiting approval. This holds while the runs are still pending, and before any run exists. The rules endpoint was unreadable, or the legacy protection endpoint gave a status other than 200 or 404 (such as a 403, a 5xx or no response) or an unrecognised 200 body. Do not treat it as `passed`. `merge` never merges in this state: it reports `blocked` when the ruleset bypass state is unknown too, and `incomplete` otherwise. A token that gets a 403 from the legacy endpoint therefore can never use `merge`. With that token, wait until every Actions run for the SHA has finished and succeeded (`gh run list --commit <sha>`), check the required checks yourself, then run `gh pr merge N --squash --match-head-commit <sha>`. |
| `merged` | 0 | GitHub reports `state == MERGED` and returned a merge commit, read back after the merge. |
| `error` | 3 | A required read failed or returned malformed JSON, for example a denied pull request or Actions request. With `mutated: true`, `gh pr merge` was called but the readback failed, so the merge outcome is unknown: read the pull request before running `merge` again. |
| `queued` | 10 | Auto-merge is enabled or the pull request is in a merge queue. It is not merged yet. |
| `pending` | 11 | The required checks are known, and Actions runs are queued or running, none exist yet, or a required check has no Actions job yet (`status` only). Also, GitHub reports the pull request merged but has not returned the merge commit yet. |
| `failed` | 20 | The latest run of a workflow, or a required check's job, failed, timed out or could not start. |
| `cancelled` | 21 | The latest run of a workflow, or a required check's job, was cancelled. |
| `blocked` | 22 | The pull request is closed, a draft or conflicting; a workflow run awaits approval; the merge method or auto-merge is disabled; a ruleset bypass is possible or unknown; or GitHub rejected the merge. |
| `superseded` | 23 | The pull request head is no longer the pinned SHA. Nothing is merged. |
| `timeout` | 24 | The deadline passed while pending, or a merge was not confirmed within 120 seconds. |
| (usage) | 2 | Bad arguments, including `merge` without `--confirm-merge` or a full `--sha`. No GitHub call is made. |

When a workflow has several runs for the same SHA and event, only the newest counts. Older runs are listed in `ci.superseded_runs`, so a run cancelled by a newer run does not decide the result. Runs of one workflow from different events, such as `push` and `pull_request`, are counted separately.

Once every run has passed, the tool reads the jobs of those runs, one request per run. `ci.required_checks` maps each required check of the base branch to the jobs with exactly that name, as `{run_id, conclusion}` entries. A job named `Validate (3.12)` or `validate` does not satisfy `Validate`. The result stays `pending` until every required check has a job, so a run of an unrelated workflow, such as the PR title check, cannot produce `passed` alone. Each matching job's conclusion counts like a run's: a required job that fails under `continue-on-error` inside a successful run gives `failed`. A required check that GitHub Actions does not produce keeps the result `pending` until the timeout. When the rules endpoint cannot be read, the legacy endpoint returns a status other than 200 or 404, `gh` gets no status from it, or it returns 200 with required checks in a shape the tool does not recognise, the result is `incomplete` (exit 12), `ci.complete` is `false` and `ci.required_checks` is `null`.

## What the evidence covers

The configured fine-grained token cannot read the Checks API or GraphQL `statusCheckRollup`. The tool reads the REST Actions runs for the exact head SHA, the same data as `gh run list --commit <sha>`, and their jobs. A `passed` result does not prove that every required status check, third-party status or review passed. GitHub enforces those when the merge runs, and its decision is final.

The required checks on `main` come from the `main-branch-protection` ruleset, read from `GET repos/{owner}/{repo}/rules/branches/main` and the rulesets API. The legacy `branches/main/protection` endpoint returns 404 for this repository. When it returns 200, the tool adds its `required_status_checks.contexts` and `required_status_checks.checks[].context` to the required checks and waits for them too. A 404 there is not proof that a branch has no protection. A 404 from the legacy endpoint adds no required checks, so legacy required checks hidden behind a 404 are not waited for; GitHub still enforces them at merge time. Any other legacy status, including a 403, another 4xx, a 5xx or no status at all, makes the required checks unknown, and the result is `incomplete`. When the rules endpoint shows no rules and the legacy endpoint is not readable, the tool reports `protection: unknown`. A 403 or 404 from any policy endpoint is recorded in `policy.access` and reported as unknown. A denied ruleset list is `null`, not an empty list.

## Tests

`tests/ci/test_pr_ci.py` drives the tool with a fake `gh`, an injected clock and an injected sleep. It never uses the network. CI runs it with the other `tests/ci` suites:

```sh
uv run --no-project --with pyyaml==6.0.3 python -m unittest discover -s tests/ci -p 'test_*.py' -v
```
