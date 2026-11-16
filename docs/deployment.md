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

The image is built once, scanned, saved, hashed and bound to its source SHA. The `image` job reports the archive's sha256 and the scanned image ID as its `sha256` and `image-id` outputs, and the artifact carries only the archive and its commit. Release downloads that run's named artifact, checks the commit, checks the archive against `needs.image.outputs.sha256`, loads it, checks the loaded image ID against `needs.image.outputs.image-id` and pushes it without rebuilding. A missing output or a mismatch fails the publish step before `docker login`. The resulting registry digest is passed to the locked SSH receiver; Compose uses `BACKEND_IMAGE` with an immutable `@sha256` reference. Publisher digest selection, transport and backend deployment require the exact `ghcr.io/scowalt/nailbiter/backend` namespace and reject historical/foreign images, including lookalike registry hostnames. A `latest` tag remains only within the clean namespace for compatibility, not deploy selection.

The receiver verifies the exact Git-bundle commit before checkout and deployment, and points the deployed checkout's origin at the canonical repository without fetching old private ancestry. Legacy SQL through `020_*` retains best-effort replay; later migrations use `ON_ERROR_STOP=1` and block restart on error. The receiver lock covers checkout, migrations and restart. Verify a recoverable [database backup](operations/database-backups.md) before migration work. Never cancel running migrations or automatically undo schema changes. After restart, compare the running container's image ID with the selected digest and require public `/health` liveness before image pruning. That endpoint is a liveness check, not a database-content or end-to-end traffic proof; controlled release acceptance still needs its separately approved operational verification.

### Deployed listing check

After a successful backend deploy, the release job runs `scripts/ci/verify-listings.sh` from the GitHub runner against the public `https://live.nailbiter.app` origin. It needs no credential, tunnel or production toggle, and it sends only GET requests with no cookies. It sets one request header itself, the fixed User-Agent `nailbiter-listing-check/1 (+https://github.com/scowalt/nailbiter)`. The .NET HTTP client adds only `Host` and `Accept-Encoding: gzip, deflate, br`.

- **Checker.** The `listing-checker` job publishes `apps/listing-check` as a self-contained Linux binary with `scripts/ci/package-listing-check.sh` and uploads it as `listing-checker-<sha>` with its commit. The job reports the archive's sha256 as its `sha256` output, not as a file in the artifact, so a step in another job that could replace the artifact cannot also replace the hash. The release job needs it, installs no SDK and builds nothing: it checks the commit, checks the archive against the job output, unpacks the binary and runs it. A mismatch, or a missing hash, fails the step with exit 2 before any request.
- **Checker job.** The binary runs in the credentialed release job, so the job that builds it runs nothing but checkout, the .NET SDK install, the repository's own scripts and the upload: no mise, pip, uv, Bun or Playwright. `test_release_verification.py` fails if the packaging moves into a job with any of them. Two inputs come from outside the repository:
  - the .NET SDK tarball, which `scripts/ci/install-dotnet-sdk.sh` downloads for the version in `apps/backend/global.json` and checks against the sha512 committed in `scripts/ci/dotnet-sdk.sha512` before it extracts anything (see [CI .NET SDK](#ci-net-sdk));
  - the `linux-x64` runtime pack from nuget.org during `dotnet publish`, which NuGet verifies against its package signature.

- **Revision.** `scripts/ci/build-image.sh` bakes the commit SHA into the image as `SourceRevision` and refuses to save an image whose environment lacks it. The backend sends that value in an `X-Nailbiter-Revision` header on every response. The check polls `/health` every 5 s for up to 120 s until the header equals the released SHA, and requires the same header on every listing response. If any other revision, or none, still answers, the check fails as `readiness`, names the expected and served revisions, and requests no listing.
- **Pages and budget.** `/`, `/nfl`, `/nba`, `/nhl`, `/mlb` and `/cfb`: one warmup and three measured rounds, a 1,500 ms median TTFB budget and a 2,500 ms median completion budget, a 10 s per-request deadline. Status, listing markup and timing are separate checks, and an empty listing passes. These numbers are the budgets [#144](https://github.com/scowalt/nailbiter/pull/144) documented for the listing check; [listing-performance-check.md](testing/listing-performance-check.md#use-after-a-deployment-8) gives their basis. Change them only in a reviewed PR that cites the reports that justify it; never raise them to make a failed release pass.
- **Traffic.** At most 25 `/health` polls and 24 listing requests per release. A run takes at most about 6 minutes: 120 s of readiness plus one 10 s poll, then 24 requests of at most 10 s each.
- **Failure.** A failure turns the step red with an `::error` annotation that names the failure class and the next step. Nothing rolls back and no data changes: the release stays deployed. The marketing deploy runs before the check, so a listing failure does not hold it back.
- **Evidence.** The job summary shows the revision, image digest, origin, verdict and the checker's text table. The `listing-check-<sha>` artifact keeps the JSON report and the text output for 90 days.

The check runs inside the `production-release` concurrency group, so a later release cannot replace the container mid-check. It does not call `require-release.sh`: it verifies the release this job just deployed, even if `main` has moved on since.

Marketing is built during validation and deployed from that same run's checked artifact, not rebuilt with credentials. The `checks` job reports the archive's sha256 as its `marketing-sha256` output, and `validate` passes it to the release job, which needs `validate`, not `checks`. The deploy step checks the commit and the archive against that output before it unpacks anything. No artifact that the release job downloads carries its own hash: a step in another job that could replace an artifact cannot also replace the hash that the release job checks it against. The release job downloads each artifact into its own directory, `.ci/artifacts/backend`, `.ci/artifacts/marketing` and `.ci/artifacts/listing-checker`, and reads each archive and commit only from there. A replaced artifact can add files that no hash covers, but only inside its own directory, so it cannot overwrite another artifact's files or the files that later release steps write to `.ci`, such as the Tailscale binaries in `.ci/bin`, which that job adds to `PATH`. `tests/ci/test_release_artifact_hashes.py` runs each chain from the producing step to the release step, with each file where `actions/download-artifact` writes it, and fails if two downloads share a directory or one is inside another. Main pushes deploy it only for its source/build/workflow paths; manual main releases deploy both apps. Empty unrelated changes do not imply marketing deployment.

### CI mise

Both `jdx/mise-action` steps in `.github/workflows/ci.yml` (`checks` and `backend-tests`) set `version`, `sha256` and `cache: false`. Each job downloads that mise release from GitHub, and the action compares `sha256` with the `mise` binary before it runs it, failing the step if they differ. When `sha256` is set, the action skips its own check against the release's signed `SHASUMS256.txt`, so step 2 does that check.

The action's cache is off because nothing checks what it restores. The cache holds the whole mise data directory: the `mise` binary and the installed Python, uv and gitleaks. On a cache hit the action runs the cached `mise` to read its version before it compares `sha256`, and mise treats the installed tools as already installed without checking them. A `mise.lock` would not help: mise checks lockfile checksums against downloads, not against installed tools. Installing those tools takes about 3 seconds per job, so `tests/ci/test_mise_version.py` requires `cache: false` on every use.

To change mise:

1. Pick a release from `https://github.com/jdx/mise/releases` that is not a prerelease and was published at least 7 days ago. Read `https://github.com/jdx/mise/security/advisories` for advisories that the release fixes or that still affect it.
2. Download `SHASUMS256.txt` and `SHASUMS256.txt.minisig` from that release. Verify the signature with `minisign -Vm SHASUMS256.txt -P <key>`, where `<key>` is `MISE_MINISIGN_PUBLIC_KEY` in `src/index.ts` of the pinned `jdx/mise-action` commit. Copy the hash of `mise-v<version>-linux-x64`, the bare binary. The `mise` binary in the `.tar.zst` archive has the same hash.
3. Set `version` and `sha256` in both steps.
4. Run `tests/ci`. `test_mise_version.py` fails if a step does not have both inputs, if the steps disagree, or if a step runs on a runner that is not x64 Linux.
5. In the pull request's `Checks` log, check that `Running mise --version` prints the new version.

### CI Bun and Trivy

Every job that runs `bun` or `bunx` (`checks` and `release`) installs it with `scripts/ci/install-bun.sh` in a step before the first one that runs it. The script downloads `bun-linux-x64.zip` from the GitHub release over HTTPS only and checks it against the sha256 in the script. Only then does it extract `bun` and add a `bunx` link to it. It runs `bun --version` and stops unless bun exits 0 and prints exactly the pinned version, so a binary that cannot start on the runner fails this step rather than a later one. `release` would otherwise fail only at `bunx wrangler deploy`, after the backend is published. Only then does the script add the directory to `PATH`. The workflow's `MISE_DISABLE_TOOLS` includes `bun`, so `jdx/mise-action` does not install the `.mise.toml` bun, which nothing hashes. Local development still gets bun from `.mise.toml`. CI does not use `oven-sh/setup-bun`, because the action has no hash input and on a cache hit it runs the restored binary with the job's `GITHUB_TOKEN` in its environment. `tests/ci/test_tool_caches.py` fails if a job runs bun without the install step before it, if mise is allowed to install bun, or if the script and `.mise.toml` name different versions. It also runs the script against a tampered download, against matching downloads whose `bun` exits 1 or prints another version, and against a matching download that works.

To change bun:

1. Pick a release from `https://github.com/oven-sh/bun/releases` that is not a prerelease and was published at least 7 days ago. Read `https://github.com/oven-sh/bun/security/advisories` for advisories that the release fixes or that still affect it.
2. Download `bun-linux-x64.zip` and `SHASUMS256.txt.asc` from that release. Import the release key with `gpg --keyserver hkps://keys.openpgp.org --recv-keys F3DCC08A8572C0749B3E18888EAB4D40A7B22B59`, the key that bun's own `dockerhub/*/Dockerfile` files use. Run `gpg --verify --output SHASUMS256.txt SHASUMS256.txt.asc`, which must print `Good signature from "Robobun <robobun@oven.sh>"`. Then run `grep ' bun-linux-x64.zip$' SHASUMS256.txt | sha256sum --check`.
3. Set `version` and `sha256` in `scripts/ci/install-bun.sh`, and the `bun` version in `.mise.toml`.
4. Run `tests/ci`. In the pull request's `Checks` log, check that `Install pinned bun` prints `bun.zip: OK` and that `bun install` prints the new version. `release` runs the same script on `main`.

The `image` job installs Trivy itself and passes `skip-setup-trivy: true` and `cache: false` to `aquasecurity/trivy-action`. The `Install pinned Trivy` step downloads `trivy_<version>_Linux-64bit.tar.gz` from the GitHub release and checks it against the sha256 in the step before it extracts `trivy` and adds it to `PATH`. With `cache: false` the action restores neither the Trivy binary nor its vulnerability database, so Trivy downloads the database from `mirror.gcr.io` each run. It did so on cache hits too, because the restored copy was out of date. `tests/ci/test_tool_caches.py` requires these inputs and runs the install step against a matching and a tampered download.

To change Trivy:

1. Pick a release from `https://github.com/aquasecurity/trivy/releases` that is not a prerelease and was published at least 7 days ago.
2. Read `https://github.com/aquasecurity/trivy/security/advisories`. Add each advisory that is not yet in `TRIVY_ADVISORIES` in `tests/ci/test_tool_caches.py`, with the first affected version and the first fixed version. The test fails if the pinned version is in any of those ranges.
3. Download `trivy_<version>_Linux-64bit.tar.gz` and its `.sigstore.json` bundle. Verify it with `cosign verify-blob --bundle trivy_<version>_Linux-64bit.tar.gz.sigstore.json --certificate-identity https://github.com/aquasecurity/trivy/.github/workflows/reusable-release.yaml@refs/tags/v<version> --certificate-oidc-issuer https://token.actions.githubusercontent.com trivy_<version>_Linux-64bit.tar.gz`.
4. Set `version` and the tarball's `sha256` in `scripts/ci/install-trivy.sh`.
5. Run `tests/ci`. In the pull request's `Build and scan backend image` log, check that `Install pinned Trivy` prints `trivy.tar.gz: OK`.

### CI .NET SDK

Every CI job that runs `dotnet` on the runner (`checks`, `backend-tests` and `listing-checker`) installs the SDK with `scripts/ci/install-dotnet-sdk.sh`, not through mise. The workflow sets `MISE_DISABLE_TOOLS` so that `jdx/mise-action` does not install the `mise-plugins/mise-dotnet-core` plugin, which would clone the plugin at its default branch and run an unpinned `dotnet-install.sh` with no hash check. Local development still uses the plugin through `.mise.toml`. The `image` job builds inside Docker from `mcr.microsoft.com/dotnet/sdk`, referenced by tag, which this script does not cover.

The script downloads `dotnet-sdk-<version>-linux-x64.tar.gz` from `builds.dotnet.microsoft.com` over HTTPS only, with TLS 1.2 or later. If the tarball does not match its line in `scripts/ci/dotnet-sdk.sha512`, the step fails with a `.NET SDK checksum mismatch` annotation and extracts nothing. If that file has no line for the `global.json` version, the step fails before it downloads anything. The next step, `scripts/ci/check-dotnet-sdk.sh`, fails unless `command -v dotnet` is the pinned install under `$RUNNER_TEMP/dotnet` and `dotnet --version` equals the `global.json` version, and it logs both values.

To change the SDK, for example to .NET 11:

1. Set the version in `apps/backend/global.json` and in `.mise.toml`.
2. Replace the line in `scripts/ci/dotnet-sdk.sha512` with the `hash` of the `linux-x64` `.tar.gz` SDK file for that version from Microsoft's `https://builds.dotnet.microsoft.com/dotnet/release-metadata/<major>.<minor>/releases.json`, followed by two spaces and the archive name.
3. Run `tests/ci`. `test_dotnet_sdk.py` fails if the three files disagree.

### CI Python packages

CI installs Python packages only from requirements files that pin every package, transitive dependencies included, to one version and list its sha256 hashes:

| Suite | Requirements file | Direct packages |
|---|---|---|
| Comment policy | `.comment-policy/requirements.txt` | the five `tree-sitter` packages at the versions in the file |
| `tests/ci` | `scripts/ci/tests-ci-requirements.txt` | `pyyaml` |
| `apps/analysis` tests | `scripts/ci/analysis-requirements.txt` | `pandas`, `sqlalchemy`, `pydantic` |

Each step creates a virtual environment under `$RUNNER_TEMP` and runs `uv pip install --require-hashes --no-build -r <file>`. uv then stops on a package that the file does not pin with `==`, on a download whose sha256 the file does not list, and on a source distribution that needs a build. CI does not use `uv run --with`: uv 0.11.32 installs from a `--with-requirements` file without checking its hashes. `tests/ci/test_python_requirements.py` reads each command line in the workflow's `run` steps and in `scripts/ci`. It covers pip, uv and pip-tools only. It fails on any of these, under any path such as `/usr/bin/pip` or `"$RUNNER_TEMP/v/bin/pip"`:

- `uv run --with`, `uvx`, `uv tool`, `pipx`, `pip-sync` or `python -m piptools`;
- a plain `pip install`;
- `uv venv --seed` or `UV_VENV_SEED=`, which install pip without a hash;
- a `uv run` whose `--no-project` is missing or comes after the command, where uv passes it to that command and still installs the project;
- a `uv run` of a script with a PEP 723 `# /// script` block, of a script it cannot read, of stdin (`-`) or of a URL. uv resolves inline dependencies without a hash check when the target ends in `.py` or `.pyw` in any case, such as `S.PY`, or when `--script`, `-s` or `--gui-script` is given. It does not read them for `uv run python script.py`. The test reads a relative script from the `--directory` that the command gives, because uv changes to that directory first;
- a `uv run` option that is not in the test's lists, because the test cannot then tell whether the option takes a value. A value hides the script: uv still resolves the dependencies of `s.py` in `uv run --no-project --env-file e s.py`. The lists hold every option that `uv run --help` prints for uv 0.11.32, written alone or as `--name=value`. The test rejects hidden options such as `--python-preference`, option clusters such as `-qq`, and short options with the value attached, such as `-p3.14`. When uv changes, compare `RUN_OPTIONS_WITH_VALUES` and `RUN_FLAGS` with the new `uv run --help`.

The test also fails on any requirements line that is not an exact pin followed only by sha256 hashes.

A word counts as uv when it is `uv`, or when it is a path ending in `/uv` where a command starts: first on the line, after `&&`, `||`, `;` or `|`, or after `NAME=value` assignments. So `mkdir -p "$RUNNER_TEMP/uv"`, `directory="$RUNNER_TEMP/uv"` and `--cache-dir "$RUNNER_TEMP/uv"` pass. `uv pip install` knows only `--python`, `-r` and `--requirement` as options that take a value, so it still rejects `uv pip install --cache-dir "$RUNNER_TEMP/uv" …` as naming a package. That fails closed: set the cache directory with `UV_CACHE_DIR` instead.

The test reads one command line at a time and splits it like a shell, without running a shell parser. These spellings still pass, and none occurs in CI today:

- `bash -c "uv sync"`, `sh -c '…'` and `eval "…"`, because the command is inside a quoted string;
- `(uv sync)`, `x=$(uv sync)` and `uv run …;uv sync` with no space around `;`, because the splitter keeps the punctuation in the word;
- `$UV sync`, `${{ env.UV }} sync` and `PIP=pip; $PIP install`, because the program name is not known until the shell runs;
- a path ending in `/uv` after another program, as in `timeout 60 /usr/bin/uv sync` or `sudo "$RUNNER_TEMP/bin/uv" sync`, because the test does not know which programs run their arguments. A bare `uv` counts anywhere, so `timeout 60 uv sync` fails;
- `UV_VENV_SEED` or another `UV_` setting that comes from an `env:` mapping, from `$GITHUB_ENV` written in an earlier step, or from a `uv.toml`, because the test reads commands and not the environment that uv starts with.

Catching these would need a shell parser and the step's environment. A reviewer has to check them by hand. `python -m venv` and `python -m ensurepip` also pass. They install the pip wheel that ships inside the pinned Python, without a download.

The test does not look for other Python installers: `virtualenv`, `poetry`, `pipenv`, `pdm`, `conda`, `easy_install` and `python setup.py install`. Each needs the tool on the runner. The `ubuntu-24.04` image (version 20261111.370.1) lists Miniconda 26.9.4 at `$CONDA`, so `"$CONDA/bin/conda" install` would run with no earlier step. It lists none of the others. The pinned Python 3.14.8 bundles pip but not setuptools, so `easy_install` and `setup.py install` would need setuptools installed first. A step that installs `virtualenv`, `poetry`, `pipenv` or `pdm` with pip or uv has to install it from a hashed requirements file, like any other package. A reviewer has to read a step that uses conda or the system Python.

Nothing updates these files automatically.

To change a version:

1. Choose a cutoff date at least 7 days before today. Only releases uploaded to PyPI before the cutoff are eligible.
2. Regenerate the file from its direct packages. For the analysis suite:

   ```sh
   printf 'pandas\nsqlalchemy\npydantic\n' | uv pip compile - --generate-hashes --no-header --no-annotate --python-version 3.14 --python-platform x86_64-unknown-linux-gnu --exclude-newer <cutoff> -o scripts/ci/analysis-requirements.txt
   ```

   For the comment policy, edit the versions in `.comment-policy/requirements.txt` and pass that file instead of `-`.
3. Run the suite that uses the file, and `tests/ci`.

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

Contract tests use fake Git/GitHub/Docker/Doppler/Tailscale executables. They exercise trust guards, clean-namespace publishing, immutable image selection, historical/lookalike namespace rejection before enrollment or backend mutation, artifact tampering, shell quoting, cleanup, lock ordering and failed migrations without production calls. A mismatched registry-reported digest stops publishing before the release image pointer and `latest` promotion; the clean namespace's SHA push has already occurred at that boundary. `tests/ci/test_release_verification.py` runs `verify-listings.sh` with a fake checker artifact for each exit code and for a tampered artifact, and checks the checker packaging and the image build's revision proof; `ServedRevisionTests` and `DeployedListingCheckTests` in `apps/backend.tests` cover the header and the checker against loopback servers. They are not live rollout evidence. Relevant application suites remain required as described above.
