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
