# RC-05 — explicit synthetic operator approval → signed API → PostgreSQL

**Draft / laboratory only.** Stacked on RC-04 PR #9 → RC-03b PR #8 → RC-03 PR #3 → RC-02 PR #2 → RC-01 PR #1. No Telegram traffic, real residents, deployed identity system, cloud model, real databases or automatic merge.

## Concrete change

The RC-04 classifier and deterministic `DemoDispatchPolicy` produce a metadata-only proposal. The new `DemoApprovalPolicy` returns an **approved command** only when all are true:

- A real **trusted authenticated operator tenant** is supplied by the caller (the production JWT boundary still lives in the pre-existing `OperatorApi`).
- The proposed tenant exactly matches the operator tenant.
- The proposal was eligible maintenance and requires human approval.
- The affirmative operator confirmation is the exact case-specific text `APPROVE <CASE-ID>`.
- The proposed category and priority are existing allowed enum values.

Any missing, tampered, wrong-case, malformed, emergency, unknown, or cross-tenant approval fails closed without a write command. The model has **no method to generate an authenticated approval**.

**Important:** This is a lower-level **approval gate**, not a finished human login/UX. In the acceptance test below, approval is supplied by a **synthetic programmed operator fixture**, not a real person. Do not call it a production human-approval implementation.

## Actual persistence qualification (not a mock)

`tests/ResidentChat.OperatorSmokeTests` has an additional scenario:

1. Start a disposable real PostgreSQL 17 (no TCP, no container network).
2. Apply ticket migration through a privileged synthetic migration owner.
3. Start the actual .NET operator HTTP API on `127.0.0.1:0` with two synthetic allowed tenants, a synthetic locally signed JWT and a distinct runtime PostgreSQL role.
4. Validate that the unapproved/mismatched proposal produces **zero** database rows.
5. Supply the exact synthetic `APPROVE ALPHA-WATER` confirmation and POST only the two approved enum fields with a signed operator JWT.
6. Expect HTTP 201, one real metadata-only `Water/Urgent/Escalated` row, tenant from the signed JWT, and independent read/mutation denials for other tenant identities.
7. Confirm the previously existing operator and supervisor JWT, RLS, escalation, and audit tests still pass.
8. Remove the disposable PostgreSQL container and database on exit.

`tools/rc-05-approved-ticket-pg-acceptance.sh <EXACT_SHA>` uses the existing WSL2 `execution.lock`, offline NuGet cache and already-installed SDK/Postgres Docker images. It verifies `RC05_TEST_ISOLATED=1` and requires connection strings in a random local socket path beginning `/tmp/rc05pg.` and a database beginning `residentchat_approval_lab_`. This does not touch an existing PostgreSQL installation or existing project services.

Smoke tests compile the OperatorSmokeTests project and exercise the approval policy with synthetic fixtures **without** connecting to PostgreSQL. The separate acceptance script actually executes HTTP, JWT and PostgreSQL.

## What is still NOT done

- There is **no real operator-facing approval screen** or logged-in operator interaction in the Telegram application.
- RC-05's accepted confirmation in integration tests is a **scripted fixture**, not proof a real human has reviewed a real incident.
- No Telegram webhook to Qwen to operator-action integration, durable Telegram dedup, retention policy, rate limiting, independent egress firewall verification or customer consent.
- Real model-Qwen evaluation stays an independent regression test. It cannot authorize ticket writes or human action.
- Avoid further features until exact-SHA WSL2 build/security and separate real PostgreSQL acceptance have passed.

**A genuine authenticated UI and a human-reviewed workflow are the next functional milestone**, pending approved identity/authorization design. Keep PR Draft and no deployments.
