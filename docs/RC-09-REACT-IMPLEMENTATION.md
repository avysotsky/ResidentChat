# RC-09 — React dispatcher implementation (synthetic only)

## Scope and placement
- `frontend/`: React 19, TypeScript, Vite 7, componentized operator login, proposals, explicit approve/reject confirmation, PostgreSQL ticket readback.
- `src/ResidentChat.Api/Operator/SyntheticConsoleApi.cs`: unchanged JWT/tenant/write authorization; optional same-origin Vite artifact routes with existing strict CSP, cache and direct-loopback restrictions. The RC-06 vanilla console remains as the fallback if the React bundle was not created. **A fallback page is not a React acceptance pass.**
- `src/ResidentChat.Api/ResidentChat.Api.csproj`: packages Vite `frontend/dist/index.html` and `frontend/dist/assets/*` only if built BEFORE .NET publish.

## Build and verification
The frontend needs a pinned offline npm dependency tree before the owner can treat the reproducible gate as PASS. No CI/WSL dependency cache was claimed or changed in this commit. Exact npm versions are specified in `package.json`; generating/verifying `package-lock.json` using the approved isolated environment remains required.

Once approved packages are cached in WSL2:
```bash
cd frontend
npm install --package-lock-only --ignore-scripts --no-audit --no-fund
npm ci --offline --ignore-scripts --no-audit --no-fund
npm run test
npm run build
cd ..
dotnet publish src/ResidentChat.Api/ResidentChat.Api.csproj -c Release -o .lab-api
```
Check that `.lab-api/operator-ui/index.html` and its local Vite hash-named JS/CSS assets exist; all must load at the loopback-bound `/operator/console` route. **Do not expose Vite on the LAN, configure proxy/CORS, or call real APIs.** `npm run dev` is developer convenience only, NOT the acceptance deployment mode.

## Safety and UX
- Lab JWT is memory-only, cleared on pagehide/logout. UI does not issue tokens.
- Server derives tenant from signed JWT; React does not offer a tenant selector or interpret scope as authorization. ALPHA/BETA separation is tested by the signed-JWT .NET integration suite, not assumed from the browser.
- Explicit `window.confirm` plus exact case-specific confirmation POST. On a POST error, the case is blocked in-tab; optional follow-up is GET only; no mutation retry. `InDoubt` is prominently displayed.
- Approved proposals trigger signed-JWT GET by ticket ID, with explicit recheck and inline status/version/errors. Status is parsed from the existing numeric TicketStatus enum (1 Open, 2 Escalated, 3 Resolved).
- No CDN, external model call, cookies, storage, tracking, raw chat data or production secrets.
- The synthetic queue is process-memory only. Reloading React resets its JWT (operator must paste a new one) and refreshes from API after login; the approved ticket readback uses PostgreSQL.

## Distinct gates
1. WSL2 automatic `restore/build/smoke` on SHA: .NET smoke only, NOT React typecheck or browser tests.
2. Isolated `npm run test`, `npm run build`, dependency audit/reproducibility.
3. .NET publish with Vite dist and loopback/CSP same-origin HTTP verification.
4. Real disposable PostgreSQL/signed-JWT Alpha/Beta accept/reject/no-duplicate acceptance.
5. Windows manual operator browser evaluation (additional, not replaced by build).
6. Local Qwen3 guarded model test (unrelated to React, does not run from browser).

No merge, Telegram, real residents, hosted AI, production deployment or new CI workflow authorized.

## RC-09 offline SHA-scoped frontend gate (added after initial React implementation)

A standalone, networkless gate lives at `tools/rc-09-react-offline-gate.sh`. It uses the same WSL2 source mirror and execution lock as the existing Draft-PR watcher, archives the **exact** SHA, and requires an already installed **offline** Node 22 image, offline npm cache, reviewed committed `package-lock.json` and .NET/NuGet image/cache. It then runs `npm ci --offline --ignore-scripts`, Node synthetic API/contract/security tests, TypeScript/Vite build, self-hosted artifact validation, and networkless `dotnet publish`; it verifies the React index and hash-named JS/CSS are really packaged. It destroys the temporary workspace and returns `NOT_RUN` if dependencies/lockfile are missing, never inventing a PASS.

This script is intentionally **not** wired into GitHub Actions or the .NET-only watcher; it does not run on the user's WSL2 automatically just by being committed. Its PASS alone still does not prove live loopback HTTP, PostgreSQL integration, or browser acceptance. It does not install Docker images, fetch npm packages, or modify the external WSL2 runner.

The npm registry is not accessible in the assistant's isolated environment. Therefore no fabricated `package-lock.json` or unverified dependency integrity hashes have been committed. A separately authorized offline cache provisioning/lockfile review step is required to close this gate; no production deployment or merge is permitted.

## RC-09 standalone asset HTTP fixture (2026-10-22)
The disposable `ResidentChat.OperatorSmokeTests` now exercises an ephemeral **synthetic asset fixture** at the actual loopback Kestrel routes. It checks index override, hash-named JS/CSS, MIME types, and strict CSP; the fixture is removed in a `finally` block. This does **not** substitute for the real Vite bundle acceptance. The existing PG acceptance script can run it only under the authorized WSL2 disposable PostgreSQL environment; the ordinary WSL2 smoke watcher merely builds that project.

The Node runner now copies only the local npm `_cacache` into disposable writable scratch space (never mounts `.npmrc` or secret-bearing live npm config) and restricts package versions to Node 22.12+. The lockfile and offline cache remain prerequisites; their absence is a NOT_RUN condition, not a PASS.

## RC-09 live Kestrel + disposable PostgreSQL end-to-end mode

At the exact pinned Git SHA, `tools/rc-07-local-browser.sh <SHA> --pg-acceptance`
starts **real ASP.NET Core Kestrel** bound to 127.0.0.1:18760 and a **real PostgreSQL 17** disposable instance (Unix socket and `--network none`, tmpfs storage). The script generates separate ephemeral signed operator JWTs for fictional ALPHA/BETA buildings and performs real HTTP / SQL verification:

- JWT-less GET is 401; a Beta JWT attempting Alpha approval is 404; real SQL tickets remain zero.
- Wrong case confirmation returns 400 with zero writes.
- Explicit correct Alpha approval returns 200 and persists exactly one ticket under Alpha tenant with database status 2 (Escalated) and version 1 and one pseudonymous audit row.
- A signed Alpha ticket GET reads version/status from PostgreSQL; Beta ticket GET is hidden (404) by server scope and RLS.
- Duplicate Alpha approval is 409; Beta reject is 200; later Beta approval is 409; final actual SQL ticket/audit counts remain 1.
- If validated Vite assets have been packaged, local React index and JS/CSS route HTTP checks run. If not, the script states React `NOT_RUN` explicitly.
- Docker containers, test DB, JWT material and scratch files are removed on exit, success or failure. No raw synthetic JWT values appear in PG acceptance output. The existing `--self-test` remains **read-only**, and `--run` remains the opt-in browser session.

**Verification status is distinct from implementation.** Creating this script and obtaining the normal WSL2 watcher PASS does **not** run this live PostgreSQL mode. It must be invoked from the owner's WSL2 host (where Docker/SDK/PostgreSQL images and existing private Git mirror are present), at the exact SHA, and its actual output reviewed. The assistant's isolated container lacks Docker, `dotnet`, PostgreSQL and direct control of the owner's WSL2 process; do not assert PG PASS unless a corresponding run is observed. The API container uses Docker host networking for direct loopback; it is *not* a general outbound firewall proof, so absolutely no real credentials or residents are allowed.


## One-command real acceptance orchestrator

`tools/rc-09-fullstack-acceptance.sh <EXACT_SHA>` is a single-script,
unprivileged WSL2 orchestrator. It reads every subsidiary script from the **same
commit in the already fetched private git mirror**, checks their Bash syntax,
then runs these sequentially:

1. `rc-07-local-browser.sh <SHA> --pg-acceptance`: real Kestrel API process,
   separate real disposable PostgreSQL 17 server, two signed synthetic JWTs,
   Alpha/Beta segregation, HTTP approval/rejection, SQL status/version/audit,
   403/404/409/400 defense and automatic Docker/tmpfs cleanup.
2. `rc-05-approved-ticket-pg-acceptance.sh <SHA>`: independently starts another
   isolated PostgreSQL and checks operator JWT claims, direct database RLS,
   tenant isolation, ticket mutation version, supervisor permissions and audit.
3. `rc-09-react-offline-gate.sh <SHA>`, **only if** a reviewed lockfile, offline
   Node image and npm cache are present. Reports `NOT_RUN` when unavailable;
   it must not be interpreted as React PASS.

The first two stages fail immediately on unexpected results; no mutation retries,
production DB, Telegram, live user credentials or hosted inference. The
last step may legitimately be `NOT_RUN` while genuine Kestrel/PostgreSQL
checks pass. **Bash syntax and the existing WSL2 .NET smoke report do not prove
that the separate live acceptance stages ran.**

The assistant's GitHub connector cannot invoke host Docker in the user's WSL2.
Until a test-execution connector is installed, one WSL command must launch this
one-shot end-to-end suite; do not fabricate a PostgreSQL PASS report.
