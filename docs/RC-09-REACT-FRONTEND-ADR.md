# ADR RC-09 — React frontend for ResidentChat

**Decision:** Accepted by product owner on 2026-10-22. **Implementation:** RC-09 React/TypeScript source committed on the Draft branch; React build/bundling and independent acceptance are separate gates, not presumed PASS. **Scope:** synthetic, local-only operator console. **Repo:** private `avysotsky/ResidentChat`.

## Chosen stack
- **React + TypeScript + Vite**, separate `frontend/` application, component-based UI.
- **ASP.NET Core .NET 10** stays the sole authorization and business-logic authority. It owns operator auth, claims-derived tenant identity, human approval, audit, and persistence rules.
- **PostgreSQL 17** stores metadata-only tickets/audit with tenant Row-Level Security; local **Ollama Qwen3** remains optional, isolated, and **advisory only**.
- API via **same-origin REST** initially; do not introduce cross-origin permissive CORS, cloud APIs or client database access.
- Use **TanStack Query** later if justified for queue/readback cache, error/retry semantics. **SignalR** is an *optional future feature*, not an RC-09 dependency. React Router only when multiple actual screens exist; avoid adding dependencies merely for a one-page demo.
- Plain locally bundled CSS initially; no CDN, external fonts, telemetry, third-party widgets, hosted analytics or external AI.

## Explicit architectural boundaries
1. RC-09 migrates the already accepted **fictional ALPHA/BETA operator flows** from the embedded vanilla JS page to React. It must not connect real Telegram or real resident messages.
2. Preserve existing backend behavior: signed operator JWT, tenant-isolated `GET /operator/console/v1/proposals`, exact case-specific POST approval/rejection, and signed-JWT `GET /operator/v1/tickets/{ticketId}`. **Never recreate these policies in client-side code as a source of truth.**
3. Existing RC-06/07 lab JWTs are *fictional*, generated offline and usable only for the disposable local experiment. Until production identity architecture is separately approved, store entered lab JWT **only in React component/module memory**; never localStorage, sessionStorage, cookies, URL, logs, error reports, git, or browser-provided persistent state. Erase on logout and tab unload. React frontend must not issue tokens.
4. Maintain hard **loopback-only** operator asset and API restrictions and existing opt-in `synthetic-lab-only` setting. Do not loosen backend authorization, CORS, CSP, host binding or CSRF/session strategy merely to make Vite development easier.
5. Build **self-hosted versioned assets**, emitted by Vite, served from the **existing .NET same-origin route**. Do not use Vite's development server as a production or permanent lab exposed to the LAN. Restrict dev server to loopback if used locally.
6. CSP stays restrictive. Ensure script modules and assets load without inline JS, `eval`, remote fetches or wildcard `connect-src`; use `textContent`/React escaping for all untrusted strings. No `dangerouslySetInnerHTML`.
7. For `Approved` proposals, display server-returned ticket **status/version inline** after approval and on page reload, with an explicit recheck button. Distinguish UI proposal state (`Approved`/`Rejected`) from PostgreSQL ticket state (`Open`/`Escalated`/`Resolved`). Read errors must be shown in the card, not only in global feedback.
8. Rejected proposals create **no SQL ticket**; unknown or ambiguous approval results must never trigger automatic POST retries. Disable buttons while requests are in flight; do not hide or downgrade server errors.
9. No React-to-Ollama connection. Browser must never see Ollama model input, internal API keys, DB credentials or tenant secrets.

## RC-09 suggested initial file layout

```text
frontend/
  package.json
  package-lock.json        # pinned after a real reproducible install
  index.html
  vite.config.ts
  tsconfig.json
  src/
    main.tsx
    App.tsx
    api/operatorApi.ts
    components/OperatorLogin.tsx
    components/ProposalList.tsx
    components/ProposalCard.tsx
    styles.css
```

Use a tiny typed API contract that matches actual .NET JSON responses; do not invent fields or status values. Separate proposal list state, button busy state and verified ticket readback. Choose tests that can run in the owner's installed **WSL2/Docker offline pipeline**; determine whether pinned Node/npm packages and images already exist before claiming build success.

## Acceptance gates (future implementation)
- Vite production build + TypeScript checking and a targeted UI regression suite.
- Static security checks: no external origin or analytics, no persistent JWT storage, no model/backend-secret leakage, no unsafe HTML injection.
- Network-level same-origin and loopback/CSP checks.
- Real **disposable** PostgreSQL 17 + signed-JWT HTTP acceptance with fictional Alpha/Beta tenants; approve Alpha water once, read `Escalated`/version, reject Beta elevator with zero SQL writes.
- Windows manual browser test should be an **additional** gate, not replaced by compiled frontend status.
- Record immutable exact-commit reports where possible; distinguish missing reports from PASS. Keep Draft, no merge/deployment until explicit decision.

## Alternatives considered
- **Angular:** not selected; heavier architecture is unnecessary for the current operator UI.
- **Blazor:** a valid .NET-native alternative, but the owner chose React.
- **Vanilla JS embedded in API:** retains only as a tested RC-06/07 laboratory reference until React parity is verified.

## Current blockers
There is **no React implementation yet**. Existing code is vanilla HTML/CSS/JS in `src/ResidentChat.Api/Operator/SyntheticConsolePage.cs`. Existing source is stacked through PR #13; no PR is merged. Authorization for live resident data and live Telegram remains absent. `docs/ORCHESTRATOR_HANDOFF_2026_10_22_REACT.md` is the current transition handoff.
