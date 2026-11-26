# Deployment (pilot server)

One Linux server (Hetzner CPX22: 2 vCPU, 4 GB, 80 GB; Ubuntu 26.04 LTS, x86_64) runs PostgreSQL, the Api, the Worker
and Caddy. The server has no .NET: Api, Worker and DevTools are published self-contained for `linux-x64`.
No secret is in the repository: connection strings live only in `/etc/synect/*.env` on the server.

## Layout on the server

| Path | Content |
|---|---|
| `/opt/synect/releases/synect-<UTC date>-<commit>/` | one release: `api/`, `worker/`, `devtools/`, `VERSION` (root-owned, read-only for `synect`) |
| `/opt/synect/current`, `/opt/synect/previous` | symbolic links to the running and the previous release |
| `/etc/synect/synect.env` | `ConnectionStrings__Synect` (role `synect_app`); root, mode 600; services and `synect-devtools` |
| `/etc/synect/owner.env` | `ConnectionStrings__SynectOwner` (role `synect_owner`); root, mode 600; only `synect-devtools --owner` (migrations, account administration) |
| `/var/lib/synect/keys` | Data Protection keys (decrypt the stored bank / Didox passwords); `synect:synect`, mode 700 |
| `/var/backups/synect` | daily backups, 14 days; root, mode 700 |
| `/etc/systemd/system/synect-api.service`, `synect-worker.service`, `synect-backup.{service,timer}` | from `deploy/systemd/` |
| `/etc/caddy/Caddyfile` | from `deploy/caddy/Caddyfile` |
| `/etc/postgresql/18/main/conf.d/synect.conf` | from `deploy/postgresql/synect.conf` |
| `/usr/local/sbin/synect-release`, `synect-devtools`, `synect-backup`, `synect-restore-test` | from `deploy/server/` |
| `/var/log/caddy/synect-access.log` | access log of Caddy (JSON, no Authorization / Cookie headers) |

Api and Worker log to journald (`journalctl -u synect-api -u synect-worker`). Logs carry error types and ids, never
connection strings, passwords, API keys or request headers. Data Protection logs a warning at the first start ("No XML
encryptor configured"): on Linux the key files are protected by file permissions only (`/var/lib/synect/keys`, 700,
user `synect`), plus the backup directory (700, root).

Demo data: run `seed-demo` with the Worker stopped (`systemctl stop synect-worker`). A running Worker starts the first
load of new connections at once with its own default fake data, so the demo data becomes a mix (seen on the pilot
server: 13 Didox documents instead of about 400). The seed stores each demo scenario in the connection's
`settings` (`fake_scenario`: seed, accounts and currencies, counterparty pool, document base time), and the fake
adapters apply it in every process, so the Worker's scheduled loads of a seeded connection generate the same data;
re-running `seed-demo` keeps the stored document base time.

## Services

- `synect-api`: listens on `127.0.0.1:5299` only. `/health` (process is up), `/ready` (200 only when the database
  answers and every migration of this build is applied; 503 `stopping` from the first moment of a graceful stop),
  `/version` (build version, commit, expected and applied schema), `/mcp`, `/ingest/1c/v1/batches`.
- `synect-worker`: the sync queue. A PostgreSQL advisory lock allows one worker per database: a second one takes no jobs
  and waits. It takes no jobs while the schema is not compatible.
- Graceful stop (`systemctl stop/restart`, SIGTERM): `Hosting:ShutdownTimeout` = 60 s, `TimeoutStopSec` = 75 s. The
  Api answers in-flight requests; the Worker takes no new jobs, lets running loads finish for up to 45 s
  (`Sync:Queue:ShutdownDrainTimeout`), then cancels the rest and returns them to `Queued` (not a failure of the
  connection; the next worker runs them again).
- Both run as the system user `synect` (no login shell, home `/var/lib/synect`) with systemd hardening
  (`ProtectSystem=strict`, only `/var/lib/synect` writable, no capabilities, `UMask=0077`), restart automatically and
  start after PostgreSQL.

## Caddy (reverse proxy)

Since 2026-11-19 Caddy serves the domain `api.synect.uz` (A 2.29.216.38, AAAA 2a01:4f9:c010:e78d::1) with automatic
HTTPS (Let's Encrypt, renewed by Caddy itself; state in `/var/lib/caddy`). HTTP redirects to HTTPS (308). Published:
`/health`, `/ready`, `/mcp`, `/ingest/1c/v1/*` (body up to 4 MB); every other path, `/version` included, answers 404.
`synect.uz` and `www.synect.uz` are not configured here (reserved for the landing page). The last line of the
Caddyfile selects the mode: `import synect_with_domain api.synect.uz` (now) or `import synect_by_ip` (plain HTTP on the
IP, only `/health` and `/ready`). The previous IP-only file is kept on the server as `/etc/caddy/Caddyfile.by-ip.bak`.

The Api runs with `Hosting__ForwardedHeaders=true` (in `synect-api.service`): it takes the client IP from Caddy's
`X-Forwarded-For` (only loopback proxies are trusted), so the per-IP limits (20 failed authentications per minute on
`/mcp` and on the 1C receiver, then 429 with `Retry-After`) count each client separately instead of all clients as
127.0.0.1.

Change: copy `deploy/caddy/Caddyfile` to `/etc/caddy/Caddyfile`, then
`caddy validate --config /etc/caddy/Caddyfile && systemctl reload caddy`.

`/version` and the rest stay reachable through an SSH tunnel:

```
ssh -L 5299:127.0.0.1:5299 root@2.29.216.38
# then, on your computer: http://127.0.0.1:5299/version, ...
```

## Build and deploy a new version

On the developer machine (Git Bash), from the repository root of a committed state:

```
deploy/build.sh                       # artifacts/synect-<UTC date>-<commit>.tar.gz (dotnet publish -c Release, self-contained linux-x64)
deploy/deploy.sh root@2.29.216.38     # uploads the newest archive and the server scripts, runs synect-release deploy
```

On Windows the SSH key usually sits in the Windows OpenSSH agent, which the Git Bash `ssh` does not see; then run
`SSH=/c/Windows/System32/OpenSSH/ssh.exe SCP=/c/Windows/System32/OpenSSH/scp.exe deploy/deploy.sh root@2.29.216.38`.
Units, the Caddyfile and `synect.conf` are not changed by `deploy.sh`; copy them by hand when they change
(`scp deploy/systemd/*.service root@...:/etc/systemd/system/`, then `systemctl daemon-reload`).

`synect-release deploy` on the server:

1. unpacks to `/opt/synect/releases/<release>`;
2. migrates with the new DevTools as `synect_owner` (`synect-devtools --owner migrate`, `lock_timeout` 5 s; a failed
   migration stops here and nothing else changes);
3. switches `current` to the new release (`previous` keeps the old one) - the systemd units start whatever `current`
   points to, so the link is switched right before the restarts;
4. restarts the Worker (drain), then the Api;
5. waits up to 90 s for `/ready` = 200. If it does not come, it switches `current` back, restarts both and fails.

Migrations must be backward compatible with the previous release (add columns and tables; drop or rename only in a
later release, once no running release uses them). The old code keeps running between step 2 and step 4, and a
rollback runs it on the newer schema.

Other commands: `synect-release status`, `synect-release list`, `synect-release prune --keep 5` (dry run; `--yes`
deletes; current and previous are never deleted).

## Rollback

```
ssh root@2.29.216.38 synect-release rollback            # to /opt/synect/previous
ssh root@2.29.216.38 synect-release rollback <release>   # to a named release (synect-release list)
```

The schema is not migrated down. If a release has to be undone together with its data changes, restore a backup
(below).

## Migrations and DevTools on the server

```
synect-devtools --owner migrate --status   # applied / pending migrations of the current release
synect-devtools --owner migrate            # apply (normally done by synect-release deploy)
synect-devtools list-tenants
```

`synect-devtools` runs DevTools of the current release as the user `synect` in a transient systemd unit with the same
environment files and keys as the services; with `--owner` it also gets `owner.env`. Migrations are never run by the
Api or the Worker at startup. The choice of a DevTools command over an EF bundle: the EF Core documentation recommends
a separate deployment step with its own identity (a bundle or a script) over migrating at application startup; the
`migrate` command uses the same engine as a bundle (`MigrateAsync` with the EF 9+ migration lock), is shipped in every
release anyway, adds `lock_timeout` and prints what it applied.

## Backups

`synect-backup.timer` runs `synect-backup` every day at 22:30 UTC (03:30 Tashkent): `pg_dump --format=custom` of the
database `synect` and a `tar.gz` of `/var/lib/synect/keys`, with a `sha256` file, in `/var/backups/synect`; files
older than 14 days are removed. Run it now with `synect-backup`, list with `synect-backup --list`. **The database dump is
useless for the stored bank / Didox passwords without the keys archive of the same day: keep them together.**

Copy the backups to your computer (PowerShell, Windows OpenSSH):

```
mkdir $HOME\synect-backups
scp "root@2.29.216.38:/var/backups/synect/synect-*" $HOME\synect-backups\
```

(or only the newest: `ssh root@2.29.216.38 "ls -t /var/backups/synect/synect-db-*.dump | head -1"`, then `scp` that
file, its `synect-keys-<same stamp>.tar.gz` and `synect-<same stamp>.sha256`). Keep the copies encrypted (for example
BitLocker): they contain client data and the keys.

### Restore

Check a backup without touching the live database (temporary database `synect_restore_check`, row counts compared
with the live database):

```
synect-restore-test                     # newest dump; or synect-restore-test /var/backups/synect/synect-db-<stamp>.dump
synect-restore-test --drop              # drop the temporary database afterwards
```

Restore over the live database (data since the dump is lost):

```
systemctl stop synect-api synect-worker
synect-backup                                            # safety copy of the current state first
runuser -u postgres -- psql -c "DROP DATABASE synect WITH (FORCE)"
runuser -u postgres -- psql -c "CREATE DATABASE synect OWNER synect_owner ENCODING 'UTF8' TEMPLATE template0"
runuser -u postgres -- pg_restore --exit-on-error --no-owner --role=synect_owner --dbname=synect /var/backups/synect/synect-db-<stamp>.dump
# keys of the same day (only if the key ring was lost or changed):
tar -C /var/lib/synect -xzf /var/backups/synect/synect-keys-<stamp>.tar.gz && chown -R synect:synect /var/lib/synect/keys && chmod 700 /var/lib/synect/keys
synect-devtools --owner migrate --status                 # the current release must be compatible (else migrate)
systemctl start synect-worker synect-api
curl -s http://127.0.0.1:5299/ready
```

After a restore the roles' privileges come from the dump; `REVOKE CONNECT ... FROM PUBLIC` and the default privileges
of `deploy/postgresql/create-roles.sql` are database settings: re-run its part after `\connect synect` if the database
was recreated by hand.

## Server setup (done once; record of the pilot server)

1. System: `apt full-upgrade`, time zone UTC, `unattended-upgrades` for security updates, ufw (only 22, 80, 443),
   SSH by key only (`/etc/ssh/sshd_config.d/10-synect.conf`: `PasswordAuthentication no`,
   `KbdInteractiveAuthentication no`, `PermitRootLogin prohibit-password`), fail2ban (sshd jail), 2 GB swap file.
2. PostgreSQL 18 from the official PostgreSQL repository (`apt.postgresql.org`, suite `resolute-pgdg`), listening on
   localhost only, `conf.d/synect.conf`; roles `synect_owner` and `synect_app` with generated passwords
   (`deploy/postgresql/create-roles.sql`); `synect_app` is neither owner nor superuser and has no BYPASSRLS.
3. Caddy from the official Caddy repository (Cloudsmith, `any-version`), `deploy/caddy/Caddyfile`.
4. User `synect` (system, no shell), `/var/lib/synect/keys` (700), `/etc/synect/*.env` (600), units enabled.
5. Backups: `synect-backup.timer` enabled.
