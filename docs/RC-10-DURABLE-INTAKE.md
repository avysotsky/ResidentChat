# RC-10 — durable Telegram metadata admission (Draft, no live activation)

This change is a **first vertical infrastructure step**, not yet a functional
dispatcher pipeline. The existing webhook and synthetic operator lab remain
separate until PostgreSQL/real Telegram acceptance is observed.

## What is added
- Migration `db/migrations/003_telegram_intake.sql`: force-RLS tenant
  partitioning and unique `(tenant_id, update_id)` preventing replays across
  restarts. Columns: only tenant GUID, Telegram update ID, HMAC group identifier,
  numeric workflow state and timestamp. No message text/user identity.
- An opt-in `ResidentChat:DurableIntake:Enabled` switch and explicit
  `Groups:N:ChatId` to `Groups:N:TenantId` config. Entire Telegram
  allowlist MUST map exactly to configured/authorized tenants; invalid config
  fails closed. Secret `ChatHashHmacKey` is distinct from JWT/audit keys.
- When on, the webhook acknowledges `accepted` only **after a real PostgreSQL
  commit**. Replayed update IDs return `duplicate` via SQL `ON CONFLICT`.
  Database failures return HTTP 503, so Telegram can retry. No writes occur
  when the feature is disabled, preserving RC-01/RC-09 synthetic tests.

## Safety limits
- **Not enabled or deployed here.** No real Telegram bot token or group IDs
  are committed. Do not run against actual resident messages without explicit
  group consent, binding/migration proof, database retention policy, and local
  Qwen/operator UX acceptance.
- Bot webhook still requires reachable HTTPS and has no long-polling worker
  yet; a local PC should eventually use `getUpdates` polling with a single
  bot-update consumer and database-backed offset/dedup.
- Model classification, an actionable React queue, and operator approval of
  these *real* updates are **not implemented by this step**. The only
  durable item is privacy-preserving intake metadata.
- TradeOps-v2 isolation: no new host service, port, Docker container,
  NPM process or PowerShell task is launched by committing this code.

## Critical safety gate: no real resident-message ACK yet

This interim schema holds **metadata only** and cannot reconstruct message text
for downstream Qwen classification after the Telegram offset is confirmed.
Therefore it is only ready with BOTH explicit
`ResidentChat:DurableIntake:Enabled=true` AND
`ResidentChat:DurableIntake:Mode=metadata-only-test-groups`.
The identical restriction applies to the polling worker through its readiness
dependency. It is for fictional messages in dedicated test groups only.

Do NOT configure a real building/resident group, even with consent, under this
interim mode. Before switching to real resident processing, implement an atomic,
durable per-message classification/dispatch handoff (or an encrypted, short-lived
content queue with retention rules) **before offset ACK**, persisted operator
proposals and an actual React queue. Do not silently clear/lose unprocessed
resident messages.

## Explicit single Telegram transport

The separate `TelegramPolling:Transport=getUpdates-only` setting is required
before the polling worker is considered ready. When
`TelegramPolling:Enabled=true` the web API's `/telegram/webhook` route
**always** returns 404, even if polling configuration is incomplete.
The worker does not call `deleteWebhook`; removing any existing remote
Telegram webhook must be an explicitly reviewed, separate operation
for the dedicated test bot only. Never configure the same bot for two
ResidentChat workers, and never share it with production.
