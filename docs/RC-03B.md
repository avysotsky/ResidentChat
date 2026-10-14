# RC-03b — local tenant-scoped approved FAQ retrieval

Status: stacked **Draft PR**, based on RC-03 `96211e71c7526aeb16ce1704d2260f9f372a5a97`. No automatic merge or real Telegram connection.

## Purpose

Provide a minimal **read-only** PostgreSQL full-text lookup of customer-approved
building FAQ materials. This is a bounded retrieval component, **not** a
resident-facing answer generator or a Qwen/RAG pipeline. The existing local
classifier, Telegram webhook and operator ticket API are unchanged.

## Contracts

- `ApprovedFaqPolicy.TryCreate` requires a nonempty **authorized tenant UUID**,
  3–200 characters of search text, no control characters, and 1–3 results.
- `PostgresApprovedFaqStore.SearchAsync` uses an Npgsql parameter for
  `tenant_id`, plain-text search input and limit; never model-generated SQL.
- A transaction-local PostgreSQL `residentchat.tenant_id` setting and
  **FORCE ROW LEVEL SECURITY** isolate tenants as defense in depth.
- The runtime login must be a dedicated non-owner, non-superuser role with
  `SELECT` only on `residentchat.approved_faq` and **no BYPASSRLS**.
- `ApprovedFaqPolicy.ValidateResults` rejects the *entire batch* if a row
  belongs to a different tenant, is malformed, unapproved, or exceeds text
  limits. No fallback to unapproved, other-tenant or remote materials.
- Only FAQ items curated and explicitly approved by authorized human
  administrators may be inserted by a **separate trusted maintenance flow**.
  Such approval/curation is not implemented and no records are bundled.
- `source_ref` identifies the approved owner-controlled document. This
  is *not* a URL fetch instruction; no network retrieval is implemented.

## Storage

Apply `db/migrations/002_approved_faq.sql` as a trusted migration owner,
**not** as the application runtime user. It creates a multilingual
`simple` PostgreSQL tsvector (no stemming), a GIN index and forced RLS.
No real resident details, addresses, message history or credentials are
included in this migration or the synthetic tests.

The runtime service requires a connection string injected through a trusted
secret provider, but RC-03b does **not** register a live connection, DI
binding, API route, scheduled import, outbound message, or cloud integration.

## Test evidence and known limits

The current WSL2 PR watcher runs
`tests/ResidentChat.SmokeTests/ResidentChat.SmokeTests.csproj` and the
separate six-fixture real-Qwen evaluator. Synthetic offline smoke tests in
this slice exercise input validation, Ukrainian text, result limits,
approval records, overlong answers and fail-closed cross-tenant results.

**No real PostgreSQL connection or RLS/FTS integration test is run by this
watcher.** A separate privileged migration step and independent live
ephemeral PostgreSQL qualification are mandatory before any integration
with an operator or real tenant. In particular, tests of RLS with a
non-owner/non-BYPASSRLS account and pooled connection reuse are required.

Full-text `simple` is lexical, not semantic: inflection, synonyms and
misspellings can miss. An empty result means **unknown**; do not fabricate
answers. Even an approved answer must be reviewed for freshness and relevance
before it could be displayed. FAQ results are **not** yet passed to Ollama.
Their text must be treated as data and may not confer model or application
authority. No hosted LLM, cloud fallback, public Telegram integration or
automatic moderator action is permitted.

## Dedicated PostgreSQL acceptance gate — prepared, separately executed

Standalone test project:
tests/ResidentChat.FaqPgAcceptance/ResidentChat.FaqPgAcceptance.csproj

Launch script: tools/rc-03b-faq-pg-acceptance.sh (invoke with bash).

The mandatory ResidentChat.SmokeTests project now references the acceptance
executable only to COMPILE it within WSL2. The regular smoke runner does not
EXECUTE the database suite, and its PASS must not be called PostgreSQL PASS.

The local launcher requires the .NET 10 SDK, initdb, pg_ctl, psql, a
pre-restored offline NuGet package cache, and an unprivileged WSL2 user.
Run from a fixed PR SHA workspace while respecting the shared execution.lock.
It creates a disposable PostgreSQL cluster under a private Unix-socket
directory; TCP is disabled, the database name starts with
residentchat_faq_acceptance_, and the runtime role is a separate
residentchat_faq_reader with SELECT only. The migration owner is distinct.
It stops PostgreSQL and deletes the temporary cluster on exit.

The program verifies on a real PostgreSQL server:
1. Trusted migration 002_approved_faq.sql succeeds; NULL approval is rejected.
2. Table RLS is enabled and forced; the runtime role is not owner, superuser,
   BYPASSRLS, CREATEDB or CREATEROLE and has SELECT-only privileges.
3. Two fabricated documents from different tenant IDs cannot cross RLS.
4. Without a scoped tenant, no rows are visible. A transaction-local tenant
   setting does not survive COMMIT or ROLLBACK or pooled connection reuse.
5. Actual Cyrillic lexical FTS returns each tenant's own item, but no
   cross-tenant item; unknown queries return none; SQL-like text is data.
6. Direct runtime UPDATE returns permission denied.
7. Cleanup works even after failure; no production database is touched.

Exact output contract proposed for WSL2 operator:
autotest-results branch, faq-pg/pr-8/<FULL_SHA>.json.
Include immutable SHA, stage statuses (offline restore, build, ephemeral
PostgreSQL, migration, test, cleanup), test exit code, and no secrets or
message bodies. Preserve existing WSL2 security and Qwen gates; run this
as a SEPARATE opt-in stage behind the existing execution.lock. Do not
use GitHub Actions, Relay Queue or cloud databases. A stage that was not
executed must be recorded as NOT_RUN, never as PASS.

Important: the current WSL2 watcher has not yet been extended to run this
new acceptance. Until a PostgreSQL execution report exists for an exact
PR #8 SHA, RC-03b is NOT database-qualified.

Tenant GUC is a defense-in-depth database filter, not proof of operator
identity. A future API must derive the tenant from a verified signed claim,
never from resident messages, model output or user-supplied parameters.
No FAQ HTTP route, runtime connection, real content, or Telegram outbound
integration is enabled by these changes.
