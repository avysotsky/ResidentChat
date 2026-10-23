# RC-10 — durable Telegram metadata admission (Draft, no live activation)

This change is a **first vertical infrastructure step**, not yet a functional
dispatcher pipeline. The existing webhook and synthetic operator lab remain
separate until PostgreSQL/real Telegram acceptance is observed.

## What is added
- Migration \`db/migrations/003_telegram_intake.sql\`: force-RLS tenant
  partitioning and unique \`(tenant_id, update_id)\` preventing replays across
  restarts. Columns: only tenant GUID, Telegram update ID, HMAC group identifier,
  numeric workflow state and timestamp. No message text/user identity.
- An opt-in \`ResidentChat:DurableIntake:Enabled\` switch and explicit
  \`Groups:N:ChatId\` to \`Groups:N:TenantId\` config. Entire Telegram
  allowlist MUST map exactly to configured/authorized tenants; invalid config
  fails closed. Secret \`ChatHashHmacKey\` is distinct from JWT/audit keys.
- When on, the webhook acknowledges \`accepted\` only **after a real PostgreSQL
  commit**. Replayed update IDs return \`duplicate\` via SQL \`ON CONFLICT\`.
  Database failures return HTTP 503, so Telegram can retry. No writes occur
  when the feature is disabled, preserving RC-01/RC-09 synthetic tests.

## Safety limits
- **Not enabled or deployed here.** No real Telegram bot token or group IDs
  are committed. Do not run against actual resident messages without explicit
  group consent, binding/migration proof, database retention policy, and local
  Qwen/operator UX acceptance.
- Bot webhook still requires reachable HTTPS and has no long-polling worker
  yet; a local PC should eventually use \`getUpdates\` polling with a single
  bot-update consumer and database-backed offset/dedup.
- Model classification, an actionable React queue, and operator approval of
  these *real* updates are **not implemented by this step**. The only
  durable item is privacy-preserving intake metadata.
- TradeOps-v2 isolation: no new host service, port, Docker container,
  NPM process or PowerShell task is launched by committing this code.
