# Database backup and restore verification

Use this procedure only within explicitly approved database/backup work. Keep production data and raw diagnostics on protected storage outside repositories, build contexts and CI artifacts. A successful application health check is not backup verification.

## Establish the recovery point

1. Verify the source container, database, data-volume identity and deployed revision. Check disk/memory headroom and coordinate with the deployment lock so backup work does not race a migration. Preserve existing backups and the running database.
2. Record the PostgreSQL server version and the database's actual Timescale extension version from metadata:

   ```sql
   SELECT current_setting('server_version_num');
   SELECT extversion FROM pg_extension WHERE extname = 'timescaledb';
   ```

   The container image tag is **not** the extension version. An image upgrade does not necessarily run `ALTER EXTENSION` in an existing database. Do not upgrade production to make a restore test pass.
3. Create a consistent custom-format `pg_dump` using the existing approved database access. Use a new, restricted destination (directory0700/file0600), reject accidental overwrites, and keep credentials out of arguments/logs. Record the file's timestamp, byte count and checksum without exporting its contents.

## Verify in isolation

Use a task-owned test container/database with compatible PostgreSQL and the **exact recorded Timescale extension version available**. Give it bounded resources, no external network or published ports, and only task-owned scratch storage. Confirm its identity before issuing database commands; never mount production data volumes into the test target.

Inside that isolated target:

1. Create an empty database from **template0**:

   ```sh
   createdb --template=template0 "$RESTORE_DB"
   ```

   The image's default `template1` can already contain a newer Timescale extension. `CREATE EXTENSION IF NOT EXISTS ... VERSION ...` then leaves the inherited version unchanged. That mismatch caused a missing-catalog-relation failure during the migration's first restore test.
2. Confirm the new database has no inherited Timescale extension, create the recorded version explicitly, and read back `pg_extension.extversion` before restoring:

   ```sql
   CREATE EXTENSION timescaledb VERSION '<recorded source version>';
   SELECT extversion FROM pg_extension WHERE extname = 'timescaledb';
   SELECT timescaledb_pre_restore();
   ```

3. Restore the complete archive with errors fatal. For an isolated data/schema verification target where original ownership and tablespace mappings are intentionally not replayed:

   ```sh
   pg_restore --exit-on-error --no-owner --no-tablespaces \
     --dbname "$RESTORE_DB" "$BACKUP_FILE"
   ```

   These options are not a blanket production recovery recipe. A real recovery must also account for required roles, privileges, tablespaces and production-local secrets; preserve the original archive metadata.
4. Run `SELECT timescaledb_post_restore();`, verify the extension version and expected schema, and confirm the archive checksum is unchanged. Retain the success/failure receipt. Schema metadata may be reported; application rows and raw diagnostics stay private.
5. Remove only the verified task-owned test container and scratch storage. Preserve the backup, its checksum and verification records. Confirm the production database/container/volume were not recreated or upgraded.

## Failure and completion boundaries

A dump file alone is not a verified recovery point. On failure, keep releases paused, retain private diagnostics and investigate the isolated restore before retrying. Never automatically restore over production, reverse SQL changes or reset data volumes.

During the repository migration, creating the restore database from template0 and installing the source's exact extension version resolved the inherited-version mismatch; the same archive then restored successfully. This establishes the tested logical recovery point, not an off-host disaster-recovery copy, an ongoing backup schedule or correctness of every application record.
