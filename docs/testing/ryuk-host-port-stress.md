# Ryuk host-port stress probe

Testcontainers 4.15.0 accepts one host port for its cleanup container, Ryuk.
`DisposableHostPorts.SelectFree` chooses that port by binding a free port in
61000-65535 and releasing it, then Docker binds it. Something else can take the
port in between (issue #102). This probe measures how often that happens. It is
too slow and too disruptive for the regular suite, so
`RyukHostPortStressProbe` is skipped unless `NAILBITER_RYUK_STRESS_ROUNDS` is
set. Run it only through the script. It disposes and restarts the test
process's Ryuk every round, which would remove containers that other tests in
the same process still use. The regular suite keeps the fast check, `RyukHostPortTests`, which asserts
that Ryuk's published port is above the OS ephemeral range.

## Run

From the repository root, with Docker on a local Unix socket and the pinned
.NET SDK:

```sh
ROUNDS=60 LISTENERS=8000 CHURN_PER_SECOND=1000 scripts/ryuk-port-stress/run.sh
```

| Variable | Default | Effect |
|---|---|---|
| `ROUNDS` | 100 | Ryuk starts. Each round starts one isolated PostgreSQL container, which starts Ryuk, then disposes both. |
| `LISTENERS` | 8000 | Listeners that `hold_ephemeral_listeners.py` holds on OS-assigned ephemeral ports. |
| `CHURN_PER_SECOND` | 200 | Held listeners closed and replaced each second (an upper bound; socket calls slow it), so the OS keeps assigning new ephemeral ports. Set it to 0 with `LISTENERS=0`. |
| `DOCKER_RANGE_LOOPS` | 0 | Parallel loops that run short-lived containers publishing a port from 61000-65535, so Docker keeps allocating ports from the range the probe uses. |

The script prints the load average before and after, the number of Ryuk starts
and failures, how many published Ryuk ports fall inside or above the OS
ephemeral range, and each failure message. With `DOCKER_RANGE_LOOPS` it also
prints how many range containers started and failed. It stops its competitor
processes and removes only the containers that carry its own label.

## Concurrent test processes

The selection starts at a random port because several test processes on one
host can choose Ryuk's port at the same moment. If every process probed upward
from 61000, they would pick the same free port and all but one Ryuk would fail
with `port is already allocated`. This script starts `RyukHostPortTests` in
several processes at once:

```sh
ITERATIONS=4 PROCESSES=8 scripts/ryuk-port-stress/concurrent.sh
```

It prints how many processes passed, how many failed with a port conflict, and
the conflicting ports. It exits non-zero if any process failed.

## Shared hosts

The competitor takes ephemeral ports from every process on the host. While it
runs, other processes that still choose a port and release it, such as test
runs on an older checkout, can fail in the same way. The range loops take
ports from 61000-65535, which isolated PostgreSQL containers use. Keep runs
short on a shared machine and expect sibling test runs to report a Ryuk or
PostgreSQL port conflict while the probe runs.
