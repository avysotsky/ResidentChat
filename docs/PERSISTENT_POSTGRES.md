# ResidentChat — permanent PostgreSQL 17 (RC-22, owner-operated)

**Goal:** a single stable PostgreSQL instance for ResidentChat on the owner's WSL2 Docker Engine, retaining application records, tenant metadata, encrypted review entries and Telegram cursor across application/container/computer restarts. This replaces ephemeral PostgreSQL **for actual running ResidentChat data**; disposable databases remain only for separate isolated automated acceptance tests. No live Telegram ingestion is enabled by the DB bootstrap.

## Stable identifiers

- Container: `residentchat-pg17`
- PostgreSQL database: `residentchat` (not recreated on every start)
- Persistent Docker named volume: `residentchat-pg17-data` (all DB data)
- Persistent Unix-socket named volume: `residentchat-pg17-socket`
- Image: already installed offline `postgres:17-bookworm`
- Networking: Docker `--network none`; `listen_addresses=` (no TCP, no forwarded ports). Future application processes connect through a **shared mounted socket volume** and must be configured with appropriate PG socket group permission; never add a public network listener.
- Container restart policy: `unless-stopped`. Docker Engine must be running; if the user deliberately stops this container, it remains stopped until manually started.
- Limits: one CPU, 768 MiB, 128 PIDs. Keep this workload isolated from TradeOps, JobRadar and Ollama.

## One-time installation

Owner calls pinned `tools/rc-22-persistent-postgres.sh <full commit SHA>` as ordinary WSL user `user` using the existing private Git mirror and WSL execution lock. The script:
1. Checks local image, source snapshot and absence/presence of exactly named managed container and volumes.
2. **New cluster only:** writes random admin/runtime passwords in `~/.local/share/residentchat/secrets`, with 0700 parent. The admin password file is bind-mounted read-only into postgres; it is accessible within its isolated container to PostgreSQL at startup. No password appears in Git, Docker environment value, command arguments, shell history or printed output.
3. Initializes PostgreSQL with SCRAM-SHA-256 authentication, no TCP networking, namespaced private Docker volumes, and a separate database `residentchat`.
4. Within a single transaction installs pinned schema migrations 001, 003, 004, 005, 006, plus a `residentchat.schema_bootstrap` ledger `version=6` **and a SHA-256 manifest of these exact migration files**. On subsequent runs, the ledger version and manifest must match or initialization stops without changing existing data. **No live update IDs or bot tokens are inserted.**
5. Creates a distinct `residentchat_lab_runtime` non-superuser, non-BYPASSRLS role with minimum table privileges. Migration owner remains `postgres`, not the API user.
6. **Existing matching cluster:** starts it only if stopped; checks that the Docker image, network and named volume mounts exactly match; verifies existing schema and role. No schema replay, credential rotation, deletion, volume replacement, SQL truncate or database recreation. If a container is missing but any named volume already exists, **STOP FOR MANUAL RECOVERY**, never generate a new cluster over the prior volume.

**Safety:** The first installation changes only the dedicated `residentchat-pg17` resources. It does not contact Telegram, create/update a Telegram offset, enable polling, bind TCP, start Qwen, register Windows services, change Git main, or access real resident data. All existing four pending Telegram updates remain untouched.

## Durability versus backup

Docker named volumes survive `docker restart`, API restarts, container recreation **when the same volume is reused**, and normal WSL/Windows reboot if the underlying Docker data remains intact. They do **not** protect against disk failure, WSL distribution deletion, Docker volume pruning, mistakes by administrators, or corruption. Never run `docker volume prune`, `docker compose down --volumes` or `docker rm -v` against ResidentChat.

Before admitting any actual resident content, design and test an **encrypted, access-restricted off-volume backup** using `pg_dump` and a restore drill. Persistent encrypted review content and application secrets must not be copied to an insecure backup. A durable schema ledger is not itself a backup.

## Test data isolation

The prior RC-14/17/18/21 automation uses temporary PostgreSQL containers/databases and intentionally cleans them up. Those are independent disposable test environments. **They do not reset the stable `residentchat` database** and must never be pointed at it. For future integration tests requiring persistence, use a separate permanent `residentchat_lab_test` DB/schema with a strict tenant and credentials; never run destructive fixtures in `residentchat`.

## Open work before real Telegram activation

- Confirm owner-run RC-22 permanent database provisioning and verify `RC22_PERSISTENT_POSTGRES_PASS`.
- Prove disk durability across a controlled restart and inspect `schema_bootstrap`, schema/table counts and RLS using a second non-destructive verification.
- Connect .NET10 API to the socket volume with the limited runtime role, local-only secrets and operator JWT/tenant mapping; do **not** use `postgres` role for the app.
- Design audited disposition/retention for RC-19's one unsupported service update, and intake of one unknown Alpha message; atomically persist/encrypt before any Telegram ACK.
- Wire `PostgresTelegramCursorStore` into a dedicated Windows DPAPI-aware polling transport with exclusive ownership. The cursor implementation is not currently connected to real polling; never treat a green lab test as permission to advance offsets.


## RC-22 owner-executed permanent DB bootstrap — PASS (2026-10-23)

Owner executed pinned `tools/rc-22-persistent-postgres.sh` from commit `5c1ed1817647d93098ac1f7aa89cde8d8f88f6a0` using ordinary WSL user. Observed output:
`RC22_NEW_PERMANENT_CLUSTER_CREATED=YES`, `RC22_POSTGRES_READY=YES`, `RC22_MIGRATIONS_APPLIED_ONCE=YES`, `RC22_DATABASE=residentchat`, `RC22_SCHEMA_VERSION=6`, `RC22_PERSISTENT_DATA_VOLUME=residentchat-pg17-data`, `RC22_PERSISTENT_SOCKET_VOLUME=residentchat-pg17-socket`, `RC22_NETWORK=NONE`, `RC22_RUNTIME_ROLE_RLS_SAFE=YES`, `RC22_TELEGRAM_POLLING=OFF`, `RC22_TELEGRAM_UPDATES_ACKED=NO`, and `RC22_PERSISTENT_POSTGRES_PASS`. Ordinary PostgreSQL NOTICE messages about non-existent tenant_isolation policies on first initialization were expected; subsequent migrations created the RLS policies. The owner has a **real, stable residentchat database on his WSL Docker host**, separate from synthetic disposable test databases. No token or resident message was inserted or transferred.

## RC-23 controlled restart and RLS durability check — PREPARED, NOT RUN

`tools/rc-23-verify-persistent-postgres.sh` is owner-operated, only targets container `residentchat-pg17`, and holds the shared WSL autotest lock. Before touching the container it checks exact managed image, network-none, named data/socket volume mounts and currently running state. As PostgreSQL admin through its isolated locally mounted secret (never printed), it reads a non-secret fingerprint of `pg_control_system().system_identifier`, the previously persisted `residentchat.schema_bootstrap` migration SHA-256 checksum and installation timestamp. It confirms the table set, FORCE RLS policies, runtime non-superuser attributes, and default-deny RLS with `SET LOCAL ROLE residentchat_lab_runtime`. Then it performs one `docker restart --time 20 residentchat-pg17`, waits for readiness, and compares the same persisted identity/fingerprint and security checks. **No DROP, TRUNCATE, CREATE/DELETE volume, schema mutation, cursor advancement, Telegram call, model call or API activation.** A failure prints only safe markers; never runs recovery by wiping volumes. No full WSL/Windows reboot test yet. A matching result proves retention across **container restart**, not disaster recovery, backups or application wiring.


## RC-23 owner-controlled PostgreSQL restart — PASS (2026-10-23)

Owner executed `tools/rc-23-verify-persistent-postgres.sh` pinned to `26e583192dda406e8d25796e53a0579a64237fc3`. Observed: `RC23_PRE_RESTART_SCHEMA_RLS_PASS`, `RC23_RESTARTING_ONLY_RESIDENTCHAT_CONTAINER=YES`, `RC23_CLUSTER_IDENTITY_PRESERVED=YES`, `RC23_MIGRATION_LEDGER_PRESERVED=YES`, `RC23_RLS_AND_RUNTIME_ROLE_PRESERVED=YES`, `RC23_DOCKER_VOLUME_PRESERVED=YES`, `RC23_DATABASE=residentchat`, `RC23_TELEGRAM_POLLING=OFF`, `RC23_UPDATES_ACKED=NO`, `RC23_PERSISTENCE_RESTART_PASS`. This confirms **on-machine stable PostgreSQL persistence across a controlled container restart**; it does not prove disk/WSL disaster recovery. The real DB was NOT dropped, reset, reseeded or migrated again.

## RC-24 .NET 10 runtime-to-permanent-DB read-only proof — PREPARED, NOT OWNER RUN

`tools/ResidentChat.PersistentDbProbe` is a compiled .NET 10 console tool referencing the ResidentChat API's Npgsql stack; it requires explicit `--persistent-readonly`. `tools/rc-24-dotnet-permanent-readonly.sh` is a pinned, one-shot **offline** WSL/Docker runner. It requires exactly the existing running managed `residentchat-pg17` container, permanent `residentchat-pg17-socket` named Docker volume, offline .NET10 image and local WSL runtime password file. It does not start, restart, create, rebuild, migrate or delete a PostgreSQL container/database. Its SDK container runs with `--network none`, read-only root FS, minimal privileges, numeric PostgreSQL UNIX socket group access and a read-only bind mount of just the local runtime password file. There is no TCP listener, no Telegram token, no bot, no Qwen or external AI access. It obtains the role password in process memory; neither secret nor connection string is printed or placed in command arguments. The environment only contains non-secret .NET/NuGet configuration.

The probe connects through a UNIX socket to `Database=residentchat`, `Username=residentchat_lab_runtime`, and uses only SELECT statements. It verifies current DB/user, Unix-socket connection, PostgreSQL 17+, the exact nine FORCE-RLS tables, empty default visibility when no tenant is set, and absence of superuser/CREATEDB/BYPASSRLS or schema-ledger read access. Expected `RC24_DOTNET_PERSISTENT_DB_CONNECTION_PASS`; the local owner result is **NOT YET KNOWN**. The WSL draft PR synthetic watcher `restore/build/test` is **PASS** at `a70413a122851b30ef1d2d5f137f28c75df017cd`, but that watcher intentionally does **not** access the permanent DB.

This verifies a direct .NET 10 connection but is **not yet** the continuously running ResidentChat HTTP API. The real API must be separately configured to use this same restricted role, persist its own JWT/crypto keys locally, bind loopback only, and pass tenant-specific accept/retry tests before enabling polling. No Telegram offset is advanced by RC-24.


## RC-24 owner-executed .NET 10 read-only permanent PostgreSQL connection — PASS (2026-10-23)

Owner ran `tools/rc-24-dotnet-permanent-readonly.sh` pinned at `25ecb5b3ead503056e7dea1ecfdbf523b26e0756`. Verified `RC24_DOTNET10_UNIX_SOCKET_CONNECTION_PASS`, `RC24_DATABASE=residentchat`, `RC24_ROLE=residentchat_lab_runtime`, `RC24_NETWORK=UNIX_SOCKET_ONLY`, `RC24_FORCE_RLS_TABLES=9`, `RC24_DEFAULT_DENY_WITHOUT_TENANT=PASS`, `RC24_ADMIN_LEDGER_INACCESSIBLE=YES`, `RC24_POSTGRES_RUNTIME_ROLE_LIMITED=PASS`, `RC24_SQL_WRITES=NONE`, `RC24_PERSISTENT_DB_MODIFIED=NO`, `RC24_DOTNET_PERSISTENT_DB_CONNECTION_PASS`. .NET workload warning was nonfatal. This is a successful owner-side read-only connection to the already persistent database, not a temporary test DB.

## RC-25 owner-triggered real ASP.NET10 API + permanent PostgreSQL restart acceptance — PREPARED, NOT RUN

Implementation: `tools/ResidentChat.PersistentApiAcceptance` references the real `ResidentChat.Api` and uses `ResidentChatApp.Build`, authenticated HTTP calls on `127.0.0.1`, and a real `Npgsql` connection string pointing to `residentchat` via `/var/run/postgresql`. Shell entry point `tools/rc-25-permanent-api-acceptance.sh` reuses existing managed `residentchat-pg17` and shared `residentchat-pg17-socket`; runs an ephemeral offline .NET10 SDK test container (`--network none`, read-only root FS) with only PostgreSQL socket GID and read-only local runtime-password-file mount. It **does not start/restart/remove/recreate** PostgreSQL, change schema, invoke Telegram, Ollama, public AI or external endpoints, or start a durable API service. Real API is bound to loopback only, started and stopped twice inside the one-shot acceptance.

The test uses two **reserved fictional tenant IDs** solely for acceptance: `25252525-2525-4525-8525-252525252525` (Alpha) and `26262626-2626-4626-8626-262626262626` (Beta). No residents or other tenant records are touched. Independent ephemeral signing and audit secrets are generated in RAM, never persisted, printed or sent to Git. The API is started with Operator enabled for **only** these two fictional tenants. Telegram, TelegramPolling, DurableIntake, ClassifiedTelegram, and OperatorConsole remain OFF. One metadata-only synthetic Water/Routine ticket is created via JWT-authenticated HTTP POST in the reserved test tenant **only if none exists**; otherwise the existing unique fixture is reused. Its PostgreSQL audit entry is persisted. The entire first ASP.NET app is shut down and a new app is started with the same DB, where the ticket is retrieved through JWT-authenticated HTTP GET. Tests require the same ticket ID/version, persistent exactly-one ticket and audit, anonymous HTTP 401 and cross-tenant Beta 404. Repeating the test does not intentionally create multiple fixtures. **This test writes and retains up to one synthetic ticket and audit row in the actual permanent database**; this is deliberate to prove persistence, not a disposable fixture. The stable database is never wiped.

Expected owner acceptance: `RC25_REAL_ASPNET10_HTTP_TO_PERSISTENT_POSTGRES_PASS`, `RC25_API_RESTART_RETENTION_PASS`, `RC25_FOREIGN_TENANT_JWT_RLS_DENY_PASS`, `RC25_PERSISTENT_API_ACCEPTANCE_PASS` and `RC25_PERMANENT_API_SMOKE_FINISHED_PASS`. This does not prove browser React operation, actual Telegram text classification, crash recovery of an in-flight update, or a continuously hosted Windows/WSL service. **RC-25 owner test not yet executed; do not claim persistence via real API verified until user supplies its output.**

