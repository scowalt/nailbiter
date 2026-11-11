# Deployment and canonical-repository cutover

## Status and authority

`scowalt/nailbiter` is the sole public development, issue/PR, CI and CD repository. The handoff completed on **2026-11-03** with [Actions release41571691550](https://github.com/scowalt/nailbiter/actions/runs/41571691550) at `67f8274e2ec136a0b4c847e8aab869067c2c2cfd`, verifying backend and marketing delivery. The old Nailbiter Woodpecker project is inactive and its webhook removed; its history is preserved.

`.github/workflows/ci.yml` provides validation and guarded production delivery. Releases require `PRODUCTION_RELEASES_ENABLED=true`, protected current `main` and the main-only `production` environment. A documentation edit is not authorization to alter credentials, controllers, data or provider activation. Operators may pause releases for approved maintenance; never re-enable a second publisher.

The original repository is privately archived historical evidence, not a continuing release system, required private test suite or source checkout dependency. Keep it private; preserve old refs, captured fixtures and archives. Move all continuing development into reviewed public records/fresh PRs without importing old ancestors or unreviewed attachments. Archival and issue/PR mutations require their own approval.

## Hosting identities and image boundary

| Component | Target / boundary |
|---|---|
| Backend/live app | Tailscale SSH `root@nailbiter-app-us`, checkout `/opt/nailbiter-app`, Docker/Caddy |
| Backend images | `ghcr.io/scowalt/nailbiter/backend`; clean history linked to this repository |
| Historical backend images | `ghcr.io/scowalt/nailbiter-app/backend`; remain private, with no canonical-repository Actions grant |
| Marketing | Cloudflare Worker `nailbiter`, `nailbiter.app` / `www.nailbiter.app` |
| Live/API origins | `live.nailbiter.app` / `api.nailbiter.app` |
| Production environment | Doppler configured on the VPS; no database/Doppler token is passed to PR jobs |
| Deploy serialization | `/opt/nailbiter-app-deploy.lock` plus non-cancelling Actions production concurrency |

The hosting targets, database and backup volumes stay unchanged. Release code targets a separate image namespace so historical image versions are not shared with this public repository. Changes to source alone do not establish live package permissions or deployment acceptance. Current runtime/access must be verified only under approved operational-read/deployment scope. Old documented origin IPs are historical, not a verified new routing instruction.

## Separate clean image history

[GitHub warns](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility#ensuring-workflow-access-to-your-package) that public forks may access private packages granted to the public repository. A private visibility label is therefore not sufficient isolation for historical images from this public repository. Environment secrets restrictions do not by themselves restrict repository-level package-token access.

- Publish only reviewed clean-source builds to `ghcr.io/scowalt/nailbiter/backend`. Before the first approved publish, verify that this namespace has no pre-existing unreviewed versions. If it does, stop and review another clean namespace; do not delete evidence or silently reuse it. Namespace availability/history is not established by these source files.
- Do not grant the canonical repository Actions access to `ghcr.io/scowalt/nailbiter-app/backend`, change its inheritance, or copy/retag its historical images into the clean package. Preserve its existing versions and the VPS's rollback access privately.
- Treat every image accessible to the public repository as potentially readable by public forks, regardless of package visibility. Preserve the clean-source, synthetic-fixture and no-build-secret boundaries for all future versions.
- Verify the VPS's access to the clean package under separate operational approval before deployment. Do not fall back to the historical package, a long-lived token in PR jobs, or a mutable tag when access fails. Historical-image recovery remains a separately approved operator procedure, not an exception to the new release guards.

## MoneyPuck production controls

The owner requested normal daily collection on 2026-10-13 and explicitly requested “use forecast ON” on 2026-11-03. Production Compose defaults qualification refresh and forecast selection on for season 2027. `MONEYPUCK_REFRESH_ENABLED=false` stops qualification collection; `MONEYPUCK_SELECTION_ENABLED=false` independently stops forecast selection. Other application hosts keep their default-off controls. Selection uses only eligible stored forecasts, after Kalshi priority and unchanged source/season/freshness checks; it performs no provider HTTP.

Source configuration is not a completed deployment or proof of usable forecasts. Review actual settings through the authorized PR/release workflow. The persisted provider-wide 24-hour admission budget and refusal/interruption state remain authoritative across deployments. The owner-approved refusal TTL blocks both inputs for at least 24 hours from observation, honoring a later valid Retry-After, even when both settings are true. Apply `036_MoneyPuckRefusalExpiry.sql` before the new collector; legacy timestamp-free denials receive one 24-hour window at first migration, and replay does not renew it or reset the daily budget/active state. Normal admission—not deletion or manual repair—can resume after both deadlines. Interrupted/unfinalized cycles stay blocked pending review. Preserve existing operator stops. No extra refresh, ledger reset or provider probe is a deployment check. See the [runbook](nhl-moneypuck-qualification.md) for remaining live-source readiness evidence.

## PlayoffStatus collection control

The owner authorized automatic NFL, NBA and MLB PlayoffStatus collection on 2026-11-11 ([ADR 0008](adr/0008-automatic-playoffstatus-collection.md)). Production Compose defaults it on; `PLAYOFFSTATUS_COLLECTION_ENABLED=false` stops it. Apply additive, repeatable `041_PlayoffStatusCollection.sql` before the worker. The per-league daily claim in `playoffstatus_collection_state` persists across deployments and restarts; do not delete its rows to force a fetch. The NHL PlayoffStatus model controls are separate and stay off.

## Validation contract

Every PR/main validation includes source policy/regressions, independent secret scanning without README exclusions, deterministic synthetic fixture checks, Bun/Biome/live-sync browser tests, deployment-contract tests, Python analysis and real isolated Timescale recovery tests, web-component build, .NET Release build and both .NET test suites, independent Astro build, icon verification, and an image scan blocking fixed HIGH/CRITICAL vulnerabilities.

The optional paid/provider TesterArmy pilot remains out of CI. Its maintained code and all ordinary development live in the canonical repository. No test/build downloads the old private repo or genuine provider fixtures.

Validation jobs use GitHub-hosted runners with read-only permissions and no production secrets/private network. A PR checkout must equal GitHub's event SHA (the merge SHA for `pull_request`), not an unrelated head. No `pull_request_target` or `workflow_run` promotion of untrusted artifacts is used.

## Release contract

`release` depends on both successful validation and image scan in the **same workflow run**. It additionally requires:

- Canonical repository `scowalt/nailbiter`.
- Protected `refs/heads/main` and a main push or explicit manual dispatch.
- `PRODUCTION_RELEASES_ENABLED=true`.
- Checkout SHA equal to the event SHA and the current main ref; stale candidates fail closed.
- Restricted GitHub `production` environment, with branch controls/review policy verified at cutover.

Configure only these required release bindings after approval:

| Binding | Purpose / boundary |
|---|---|
| `GITHUB_TOKEN` (`packages: write` only in release job) | Publish the exact scanned backend image to the verified-clean `nailbiter/backend` package; no access grant to the historical package |
| `TAILSCALE_CI_OAUTH_SECRET` | Dedicated ephemeral `tag:ci` enrollment and authenticated Tailscale SSH; not an unrelated project's client |
| `CLOUDFLARE_API_TOKEN` | Existing narrowly scoped Nailbiter Worker deployment access |

No credential values belong in source, comments, outputs or diagnostic screenshots. Preserve the VPS's existing package-pull credential and production-local secret manager; separately verify its clean-package access without exposing values. Review the clean package's Actions access independently of source linkage/visibility. Routine publishing needs Write, not Admin. Package grants are repository-level, not an independent main-branch/job boundary: keep write permission confined to the release job in the reviewed workflow and review maintainer/App access and future workflow changes.

The image is built once, scanned, saved, hashed and bound to its source SHA. Release downloads that run's named artifact, checks commit/bytes/image ID, loads it and pushes it without rebuilding. The resulting registry digest is passed to the locked SSH receiver; Compose uses `BACKEND_IMAGE` with an immutable `@sha256` reference. Publisher digest selection, transport and backend deployment require the exact `ghcr.io/scowalt/nailbiter/backend` namespace and reject historical/foreign images, including lookalike registry hostnames. A `latest` tag remains only within the clean namespace for compatibility, not deploy selection.

The receiver verifies the exact Git-bundle commit before checkout and deployment, and points the deployed checkout's origin at the canonical repository without fetching old private ancestry. Legacy SQL through `020_*` retains best-effort replay; later migrations use `ON_ERROR_STOP=1` and block restart on error. The receiver lock covers checkout, migrations and restart. Verify a recoverable [database backup](operations/database-backups.md) before migration work. Never cancel running migrations or automatically undo schema changes. After restart, compare the running container's image ID with the selected digest and require public `/health` liveness before image pruning. That endpoint is a liveness check, not a database-content or end-to-end traffic proof; controlled release acceptance still needs its separately approved operational verification.

Marketing is built during validation and deployed from that same run's checked artifact, not rebuilt with credentials. Main pushes deploy it only for its source/build/workflow paths; manual main releases deploy both apps. Empty unrelated changes do not imply marketing deployment.

## Migration record and future handoff safeguards

The 2026-11-03 handoff followed the sequence below. It is a reference for future controller/repository migrations, not an outstanding activation checklist. Any new operational handoff still needs explicit scope and fresh evidence.

1. Review/test the clean source, synthetic coverage, MIT/third-party notices and all public metadata. Obtain exact-name private repository creation and push/build approval before bootstrap.
2. Bootstrap privately with validation-only Actions. Keep the old controller as the sole temporary release owner. Establish tested `Validate` and `Build and scan backend image` required contexts on the new repository, preserving equivalent branch safeguards.
3. Review the last source delta and every continuing old issue/PR. Port reviewed work into the new repository without private ancestry; update developer/agent remotes and repo-specific integrations. Do not abandon active work or bulk-copy sensitive discussions.
4. Verify clean-package namespace/history, canonical Actions publishing access and VPS pull access; leave the historical package private and unshared with the canonical repository. Review the production environment and credential bindings. Do not apply the old OpenTofu state with a changed repository name: it could rename or modify the old repository. Use a separately reviewed resource/state migration; never run a broad VPS/DNS/auth-key apply for this source migration.
5. With explicit controller/deployment approval, drain old jobs, disable old repo-specific Woodpecker scheduling/webhook and any other old publishers, and verify none remain in flight. Only then arm new-repository Actions releases. Do not change shared runner infrastructure or other projects.
6. Accept a controlled release from the new repository: exact source/image, migration result, backend health and marketing identity. Preserve data/backups/DNS/provider flags. A failure stops publication; rollback needs an approved path with one controller, not simultaneous publishers or automatic SQL undo.
7. Audit the new repository's reachable commits, issue/PR bodies and all new workflow logs/artifacts. Verify public forks cannot reach production credentials/private runners and the canonical repository has no Actions grant to historical private packages. Obtain explicit public-visibility approval, publish and read back anonymously/authenticated.
8. After acceptance, apply an approved migration notice/work dispositions, retire residual old automation and archive the old repository privately. Verify external remediator/scheduler targets separately; archiving source does not stop an independent process.

## Local validation without production

```sh
python scripts/test-fixtures/generate.py --check
uv run --no-project --with pyyaml==6.0.3 python -m unittest discover -s tests/ci -v
python -m unittest discover -s tests/deployment -v
bash -n scripts/ci/*.sh
```

Contract tests use fake Git/GitHub/Docker/Doppler/Tailscale executables. They exercise trust guards, clean-namespace publishing, immutable image selection, historical/lookalike namespace rejection before enrollment or backend mutation, artifact tampering, shell quoting, cleanup, lock ordering and failed migrations without production calls. A mismatched registry-reported digest stops publishing before the release image pointer and `latest` promotion; the clean namespace's SHA push has already occurred at that boundary. They are not live rollout evidence. Relevant application suites remain required as described above.
