# RC-06 — browser operator console (synthetic, local-only)

**Status:** Draft, standalone laboratory functionality; stacked on RC-05 PR #10 → RC-04 PR #9 → RC-03b PR #8 → RC-03 PR #3 → RC-02 PR #2 → RC-01 PR #1.

This is an actual .NET 10 HTTP+HTML+JavaScript operator console, **not** a drawing or a static screenshot. It is still intentionally **NOT** a deployable customer UI. No real resident messages, Telegram groups or production credentials are allowed. There is no automatic action from Qwen.

## Functional scope

- Visit `/operator/console` on a directly bound loopback .NET 10 host (not a reverse proxy). The HTML/CSS/JS are embedded in the application and use no CDN, external JS, fonts or telemetry.
- Enter a *synthetic lab* signed operator JWT; it is kept **only in the browser-tab JavaScript memory** and erased from the input after connection. No cookies/localStorage/sessionStorage. Use synthetic JWT only; a real secure identity/session/CSRF design remains a production blocker.
- The `GET /operator/console/v1/proposals` endpoint requires a valid JWT and derives an authorized building tenant from the claims. It returns **only synthetic proposals for that tenant**, not resident text or arbitrary chat content.
- Two built-in fictional maintenance cases exist (one per first two configured tenant IDs, ordered by GUID): `ALPHA-WATER` = Water/Urgent and `BETA-ELEVATOR` = Elevator/Routine. Nothing comes from Telegram or live AI.
- **Approve** requires explicit browser confirmation and the exact case-specific `{"confirmation":"APPROVE ALPHA-WATER"}` POST. Backend independently validates signed operator identity, tenant scope, approved enum values and `DemoApprovalPolicy`. Only then does it call the existing `PostgresTicketStore.CreateAsync`, writing one actual metadata-only PostgreSQL ticket and pseudonymized audit event.
- **Reject** requires `{"confirmation":"REJECT BETA-ELEVATOR"}` and updates only volatile in-process state (no SQL write).
- Duplicate approve, approval after rejection, mismatched confirmation and cross-tenant requests are refused. If a PostgreSQL commit outcome is ambiguous, the item is `InDoubt`; it is **never retried automatically**.
- After approving, the operator can click **Проверить статус**; this uses the existing signed-JWT `GET /operator/v1/tickets/{ticketId}` with tenant RLS. The operator console does **not** resolve tickets or auto-close incidents.

The inbox is intentionally **volatile**: it is reset when the test host restarts. This is suitable only for a disposable synthetic laboratory, **not** a persistent production queue. No unknown real building, resident, person or apartment data is stored. There is no notion of real AI suggestions, durable deduplication or a real-world emergency SLA.

## Fail-closed configuration

No routes are mapped unless the full `ResidentChat:Operator` configuration is valid **AND** the following opt-in laboratory settings are BOTH present:

```text
ResidentChat__OperatorConsole__Enabled=true
ResidentChat__OperatorConsole__Mode=synthetic-lab-only
```

All three asset routes, proposal reads and approval/rejection endpoints independently refuse non-loopback local **and** remote socket addresses. The UI has a restrictive Content Security Policy, disables cache/referrer, and never uses JavaScript `innerHTML` to render server data. Operator endpoints use `RequireAuthorization`, accept only bounded, strict JSON, and do not set cookies or CORS allowances. No remote demo exposure is permitted.

**Do not copy credentials or tenant IDs from tests into any real service.** The frontend deliberately does not issue JWTs or bypass authentication.

## Verification

1. The existing WSL2 `ResidentChat.SmokeTests` gate checks that the operator UI cannot be enabled without valid operator settings (even when its own switch is set). All RC-01–RC-05 synthetic security cases still run.
2. `tests/ResidentChat.OperatorSmokeTests` now tests real **signed JWT + HTTP + PostgreSQL 17** via Kestrel on loopback and a separate non-superuser database role:
   - UI and CSP accessible only with operator config + explicit console lab switch;
   - no JWT → 401; unknown tenant JWT → 403;
   - tenant A only sees ALPHA-WATER; tenant B only sees BETA-ELEVATOR;
   - no SQL writes on list or invalid/wrong-tenant approvals;
   - explicit signed approve creates **exactly one** PostgreSQL ticket;
   - duplicate approval → 409; rejection creates **no** ticket;
   - original protected ticket read returns 200 for same tenant and 404 for a different tenant.
3. Manual real-PostgreSQL acceptance: use existing `tools/rc-05-approved-ticket-pg-acceptance.sh <FULL_SHA>` **with no additional arguments**, against the RC-06 HEAD. It creates a disposable, networkless PostgreSQL 17 container and .NET 10 SDK Docker test. The fixed `RC05_TEST_ISOLATED=1` constraint prevents accidental production connections and cleanup destroys the database.

Do NOT claim RC-06 PostgreSQL acceptance PASS from the separate WSL synthetic build gate. It must actually be executed at the exact SHA.

## Deployment blockers

This slice does not provide a real operator identity issuer, MFA, key rotation, stable verified browser session, persistent/transactional proposals with long-lived idempotency, durable human approval audit separate from ticket audit, rate limits, customer-approved retention/deletion, host firewall proof, trained safety/escalation staff, live Telegram authorization, real AI → inbox wiring, production admin authorization or tested incident recovery.

Do not connect a real Telegram group, publish a listener beyond loopback, merge automatically, or use this build to process resident data. Owner acceptance and independent security review are required before any pilot.
