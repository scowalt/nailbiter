#!/usr/bin/env python3

import argparse
import os
import re
import shutil
import signal
import statistics
import subprocess
import sys
import tempfile
import time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

PROJECT = "apps/backend.tests/backend.tests.csproj"
DEFAULT_FILTER = "FullyQualifiedName~backend.tests.ListingPerformanceTests"
PHASES = ["container", "database", "host", "schema", "seed", "body", "dispose"]
SUMMARY = re.compile(r"(Passed|Failed)!\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)")


def run_group(command, cwd, env=None, log=None):
    process = subprocess.Popen(command, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, start_new_session=True)
    try:
        output, _ = process.communicate()
    except BaseException:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait()
        raise
    if log:
        log.write_text(output)
    return process.returncode, output


def export(repo, ref, target):
    target.mkdir(parents=True)
    archive = subprocess.Popen(["git", "-C", repo, "archive", ref], stdout=subprocess.PIPE)
    subprocess.run(["tar", "-x", "-C", str(target)], stdin=archive.stdout, check=True)
    if archive.wait() != 0:
        sys.exit(f"git archive {ref} failed")


def build(tree, out, side):
    code, _ = run_group(["dotnet", "build", PROJECT, "-c", "Release"], tree, log=out / f"build-{side}.log")
    if code != 0:
        sys.exit(f"build of {side} failed; see {out / f'build-{side}.log'}")


def run_tests(tree, selection, timings, log):
    env = dict(os.environ, NAILBITER_FIXTURE_TIMINGS=str(timings))
    started = time.monotonic()
    code, output = run_group(["dotnet", "test", PROJECT, "-c", "Release", "--no-build", "--filter", selection],
                             tree, env=env, log=log)
    wall = time.monotonic() - started
    match = SUMMARY.search(output)
    failed, passed, skipped = (int(match[2]), int(match[3]), int(match[4])) if match else (-1, -1, -1)
    return code, wall, passed, failed, skipped


def phase_totals(timings):
    totals = defaultdict(float)
    cases = 0
    if timings.exists():
        for line in timings.read_text().splitlines():
            owner, phase, ms = line.split("\t")
            totals[phase] += float(ms) / 1000
            cases += phase == "body"
    return totals, cases


def main():
    parser = argparse.ArgumentParser(description="Interleaved A/B timing of a backend.tests selection between two git refs.")
    parser.add_argument("--before", required=True, help="baseline git ref")
    parser.add_argument("--after", required=True, help="candidate git ref")
    parser.add_argument("--runs", type=int, default=3, help="recorded runs per side (default 3)")
    parser.add_argument("--filter", default=DEFAULT_FILTER, help="dotnet test --filter expression")
    parser.add_argument("--out", type=Path, help="directory for logs and TSV results (default: a new temp dir)")
    args = parser.parse_args()
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))

    repo = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
    out = (args.out or Path(tempfile.mkdtemp(prefix="nailbiter-listing-timing-results-"))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="nailbiter-listing-timing-trees-"))
    sides = {"before": args.before, "after": args.after}
    shas = {side: subprocess.run(["git", "-C", repo, "rev-parse", ref], capture_output=True, text=True, check=True).stdout.strip()
            for side, ref in sides.items()}
    rows = []
    try:
        for side, sha in shas.items():
            export(repo, sha, work / side)
            build(work / side, out, side)
        for side in sides:
            run_tests(work / side, args.filter, work / f"warmup-{side}.tsv", out / f"warmup-{side}.log")
        for index in range(1, args.runs + 1):
            for side in sides:
                timings = out / f"timings-{side}-{index}.tsv"
                load_before = os.getloadavg()[0]
                started = datetime.now(timezone.utc).isoformat(timespec="seconds")
                code, wall, passed, failed, skipped = run_tests(work / side, args.filter, timings, out / f"test-{side}-{index}.log")
                totals, cases = phase_totals(timings)
                rows.append(dict(side=side, run=index, started=started, load_before=load_before, load_after=os.getloadavg()[0],
                                 exit=code, passed=passed, failed=failed, skipped=skipped, cases=cases, wall=wall, **totals))
                print(f"{side} run {index}: wall {wall:.1f}s exit {code} passed {passed} failed {failed} "
                      f"load {load_before:.1f}->{rows[-1]['load_after']:.1f}", flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    columns = ["side", "run", "started", "load_before", "load_after", "exit", "passed", "failed", "skipped", "cases", "wall", *PHASES]
    with open(out / "runs.tsv", "w") as table:
        table.write("\t".join(columns) + "\n")
        for row in rows:
            table.write("\t".join(f"{row.get(c, 0):.2f}" if isinstance(row.get(c, 0), float) else str(row.get(c, 0)) for c in columns) + "\n")

    print(f"\nrefs: before {shas['before']} after {shas['after']}; selection: {args.filter}; nproc {os.cpu_count()}")
    print(f"{'metric (s)':<12}{'before median':>15}{'range':>17}{'after median':>15}{'range':>17}{'change':>9}")
    for metric in ["wall", *PHASES]:
        values = {side: [row.get(metric, 0.0) for row in rows if row["side"] == side] for side in sides}
        before, after = statistics.median(values["before"]), statistics.median(values["after"])
        change = f"{(after - before) / before:+.0%}" if before else "n/a"
        print(f"{metric:<12}{before:>15.2f}{min(values['before']):>8.2f}-{max(values['before']):<8.2f}"
              f"{after:>15.2f}{min(values['after']):>8.2f}-{max(values['after']):<8.2f}{change:>9}")
    bad = [row for row in rows if row["exit"] != 0 or row["failed"] != 0 or row["skipped"] != 0]
    print(f"\nresults: {out / 'runs.tsv'}")
    if bad:
        print(f"{len(bad)} run(s) failed or skipped tests; their timings are not comparable", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
