# Overload safeguards

The VPS shares 2 GiB of RAM between the app, PostgreSQL, and monitoring services.
Swap is disk space that Linux uses as extra memory.
The 2 GiB swap file provides a reserve during brief memory spikes.
It does not replace query fixes or enough RAM for sustained load.

PostgreSQL cancels application queries after 30 seconds.
The app sends `Options=-c statement_timeout=30000` when it opens a database connection.
This server-side limit still works if the client does not cancel a query.
It also applies to background jobs and manual imports that use the app connection.

Deployment migrations use separate `psql` connections without these startup options.
No database-wide or role-wide timeout changes.
No VPS resize or reboot is required.
Loading a changed connection string recreates the backend container and briefly interrupts live connections.

## Apply the configuration

New servers receive `/swapfile` through `opentofu/cloud-init.yaml.tftpl`.
The deployment workflow runs `scripts/ensure-swap.sh` for existing servers.
The script accepts an existing, valid 2 GiB swap file without formatting it again.
It refuses symbolic links, incompatible files, and creation on filesystems other than ext4.
It preserves other swap entries and saves a backup before it changes `/etc/fstab`.

For an existing server, use these steps:

1. Connect to the VPS through Tailscale SSH.
2. From `/opt/nailbiter-app`, run `bash scripts/ensure-swap.sh` as root.
3. Run `swapon --show --bytes` and make sure that `/swapfile` is active.
4. Run `stat -c '%s %U %a' /swapfile` and make sure that it reports `2147483648 root 600`.
5. Make sure that `/etc/fstab` contains one `/swapfile none swap sw 0 0` entry.
6. Deploy the backend through the normal workflow to load the connection string from `docker-compose.yml`.

A live configuration applied before its PR merges is temporary.
Merge that PR before another deployment, or the deployment can restore the old connection string.
Do not print the connection string from a running container because it can contain credentials.

## Test and monitor

The database integration test reads the actual Compose configuration and opens an isolated PostgreSQL instance.
It makes sure that the app connection uses a 30-second statement timeout.
It disables the client timeout and makes sure that PostgreSQL cancels `pg_sleep(35)` itself.
It also makes sure that a separate migration connection retains `statement_timeout=0`.

Run the tests with Docker available:

```bash
scripts/test-backend csharp
```

If the rootless Docker environment cannot start Ryuk, disable that cleanup helper for the local test run:

```bash
TESTCONTAINERS_RYUK_DISABLED=true scripts/test-backend csharp
```

The tests dispose their PostgreSQL containers during normal completion.
If a test process stops abruptly with Ryuk disabled, remove only the containers from that test run.
OOM means that the kernel cannot allocate enough memory.
On the VPS, monitor memory availability, swap use, query cancellations, container restarts, and kernel OOM events.
Sustained swap activity or repeated cancellations require workload investigation, not a longer timeout alone.

## Roll back

To remove the query limit, remove `Options=-c statement_timeout=30000` from the backend connection string and redeploy only the backend.
Do not change the database-wide timeout or restart PostgreSQL.
The application still retains its existing client-side command timeout.

Do not disable swap while RAM is under pressure.
Moving swapped pages back into RAM can cause another memory failure.
When enough RAM is available, run `swapoff /swapfile`, remove only its entry from `/etc/fstab`, and remove `/swapfile`.
Also remove the swap declaration and deployment setup step from the repository, or a future deployment restores the reserve.
