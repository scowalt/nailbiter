# Deployment and canonical-repository cutover

## Status and authority

This clean-source candidate targets `scowalt/nailbiter` as the sole public development, issue/PR, CI and CD repository. **No handoff, production deployment or publication is established by these files.** `.github/workflows/ci.yml` provides validation and guarded release code. Releases stay inert unless an authorized operator sets repository variable `PRODUCTION_RELEASES_ENABLED=true` after the old controllers are drained and disabled.

The existing private repository is historical evidence after cutover, not a continuing release system, required private test suite or source checkout dependency. Keep it private; preserve old refs, captured fixtures and archives. Move all continuing development into reviewed public records/fresh PRs without importing old ancestors or unreviewed attachments. Archival and issue/PR mutations require their own approval.

## Runtime identities remain stable

| Component | Existing target |
|---|---|
| Backend/live app | Tailscale SSH `root@nailbiter-app-us`, checkout `/opt/nailbiter-app`, Docker/Caddy |
| Backend image | `ghcr.io/scowalt/nailbiter-app/backend` |
| Marketing | Cloudflare Worker `nailbiter`, `nailbiter.app` / `www.nailbiter.app` |
| Live/API origins | `live.nailbiter.app` / `api.nailbiter.app` |
| Production environment | Doppler configured on the VPS; no database/Doppler token is passed to PR jobs |
| Deploy serialization | `/opt/nailbiter-app-deploy.lock` plus non-cancelling Actions production concurrency |

Changing the source repository does not require changing domains, Worker, VPS, image namespace, database or backup volumes. Current runtime/access must be verified only under approved operational-read/deployment scope. Old documented origin IPs are historical, not a verified new routing instruction.

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
| `GITHUB_TOKEN` (`packages: write` only in release job) | Publish the exact scanned backend image; verify the new repository has Actions write access to the existing package |
| `TAILSCALE_CI_OAUTH_SECRET` | Dedicated ephemeral `tag:ci` enrollment and authenticated Tailscale SSH; not an unrelated project's client |
| `CLOUDFLARE_API_TOKEN` | Existing narrowly scoped Nailbiter Worker deployment access |

No credential values belong in source, comments, outputs or diagnostic screenshots. Preserve the VPS's read-only package-pull credential and production-local secret manager. A newly created repository does not automatically gain the old GHCR package's permissions; review package Actions access independently of source linkage/visibility.

The image is built once, scanned, saved, hashed and bound to its source SHA. Release downloads that run's named artifact, checks commit/bytes/image ID, loads it and pushes it without rebuilding. The resulting registry digest is passed to the locked SSH receiver; Compose uses `BACKEND_IMAGE` with an immutable `@sha256` reference. A `latest` tag remains only for compatibility, not deploy selection.

The receiver verifies the exact Git-bundle commit before checkout and deployment, and points the deployed checkout's origin at the canonical repository without fetching old private ancestry. Legacy SQL through `020_*` retains best-effort replay; later migrations use `ON_ERROR_STOP=1` and block restart on error. The receiver lock covers checkout, migrations and restart. Never cancel running migrations or automatically undo schema changes. After restart, compare the running container's image ID with the selected digest and require public `/health` liveness before image pruning. That endpoint is a liveness check, not a database-content or end-to-end traffic proof; controlled release acceptance still needs its separately approved operational verification.

Marketing is built during validation and deployed from that same run's checked artifact, not rebuilt with credentials. Main pushes deploy it only for its source/build/workflow paths; manual main releases deploy both apps. Empty unrelated changes do not imply marketing deployment.

## Approved cutover sequence

1. Review/test the clean source, synthetic coverage, MIT/third-party notices and all public metadata. Obtain exact-name private repository creation and push/build approval before bootstrap.
2. Bootstrap privately with validation-only Actions. Keep the old controller as the sole temporary release owner. Establish tested `Validate` and `Build and scan backend image` required contexts on the new repository, preserving equivalent branch safeguards.
3. Review the last source delta and every continuing old issue/PR. Port reviewed work into the new repository without private ancestry; update developer/agent remotes and repo-specific integrations. Do not abandon active work or bulk-copy sensitive discussions.
4. Review new-repository package access, production environment and credential bindings. Do not apply the old OpenTofu state with a changed repository name: it could rename or modify the old repository. Use a separately reviewed resource/state migration; never run a broad VPS/DNS/auth-key apply for this source migration.
5. With explicit controller/deployment approval, drain old jobs, disable old repo-specific Woodpecker scheduling/webhook and any other old publishers, and verify none remain in flight. Only then arm new-repository Actions releases. Do not change shared runner infrastructure or other projects.
6. Accept a controlled release from the new repository: exact source/image, migration result, backend health and marketing identity. Preserve data/backups/DNS/provider flags. A failure stops publication; rollback needs an approved path with one controller, not simultaneous publishers or automatic SQL undo.
7. Audit the new repository's reachable commits, issue/PR bodies and all new workflow logs/artifacts. Verify public forks cannot reach production credentials/private runners. Obtain explicit public-visibility approval, publish and read back anonymously/authenticated.
8. After acceptance, apply an approved migration notice/work dispositions, retire residual old automation and archive the old repository privately. Verify external remediator/scheduler targets separately; archiving source does not stop an independent process.

## Local validation without production

```sh
python scripts/test-fixtures/generate.py --check
uv run --no-project --with pyyaml==6.0.3 python -m unittest discover -s tests/ci -v
python -m unittest discover -s tests/deployment -v
bash -n scripts/ci/*.sh
```

Contract tests use fake Git/GitHub/Docker/Doppler/Tailscale executables. They exercise trust guards, immutable image selection, artifact tampering, shell quoting, cleanup, lock ordering and failed migrations without production calls. They are not live rollout evidence. Relevant application suites remain required as described above.
