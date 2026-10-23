# RC-10: encrypted test-group human review (not production)

Status: **Draft PR #15, code only**. All features disabled unless explicit
local/test-only configuration is supplied. No real Telegram groups, credentials,
resident data, external model or PowerShell/Docker workload was activated.

## Complete bounded text handoff

An allowlisted Telegram group message is classified by local Qwen at
127.0.0.1. Valid maintenance routes to an actionable human-approval proposal.
Emergency, conflict, FAQ, spam, uncertain model output, local model timeout
and non-text messages route to a **separate human-review queue**. All admission
paths commit a tenant-scoped `telegram_intake` row plus either a pending
proposal or an encrypted `telegram_reviews` row **in one PostgreSQL transaction**.
Only after commit does the webhook acknowledge, or getUpdates advance its
offset. Replay checks database integrity before repeating an Ollama call.

## Confidentiality / access

- Text (up to Telegram's 4096-character bound) is encrypted *locally* with
  AES-256-GCM and a fresh 96-bit nonce. Ciphertext is authenticated with
  tenant ID + update ID + HMAC(group ID) associated data. The AES key is
  supplied via protected `ResidentChat:TelegramReview:EncryptionKeyBase64`
  local config/environment (32 decoded bytes). Never commit or paste it into
  shell commands, logs or GitHub. Key rotation/recovery is not yet built.
- The PostgreSQL `005_encrypted_telegram_reviews.sql` migration adds FORCE
  RLS on reviews and read-audit rows. Indexes and a **7-day expiration**
  timestamp support a future owner-controlled physical purge. Expiry checks
  hide expired data from the API, **but do not physically delete it**. Until
  an audited purge and backup-retention policy exist, do not claim erasure.
- `GET /operator/console/v1/reviews` returns at most 50 tenant-scoped
  metadata records with reason/timestamps, never text. A second,
  explicitly requested signed and loopback-only
  `GET /operator/console/v1/reviews/{updateId}` decrypts exactly one
  unexpired record and atomically appends a pseudonymous read-audit entry.
  Both routes set no-store, and neither endpoint is registered unless the
  explicit review mode is ready.
- React never auto-decrypts. The operator selects *Переглянути*, and only
  this exact update's text appears in a transient component; it is scrubbed
  on hide, disconnect, or navigation. No browser storage, cookie, URL token,
  telemetry, hosted LLM or automatic ticket approval.

## Explicit gating

Old metadata-only lab remains separate. New reviewed classification requires:
`DurableIntake:Enabled=true`, `Mode=metadata-only-test-groups`,
`DurableProposals:Enabled=true`, `Mode=synthetic-postgres-only`,
`TelegramReview:Enabled=true`, `Mode=encrypted-test-groups-only`,
a valid 32-byte base64 local AES key, and
`ClassifiedTelegram:Enabled=true`,
`Mode=maintenance-and-review-test-groups`, with the approved local
Qwen URL/model. No implicit production activation.

## Known limits before actual resident activation

- Attachment binaries/photos/voice and non-`message` Telegram update kinds
  are not retained. The review queue for non-text group `message` events has
  metadata/reason but cannot reconstruct the original media; a separate
  consent-safe attachment policy is needed.
- A message >4096 characters or malformed Telegram input is not
  acknowledged; finite Telegram retention and external outages still exist.
- The encryption key needs local secure storage, rotation, backup policy and
  owner-controlled periodic physical purge. Runtime role has **no DELETE**.
- The current React operator uses **fictional short-lived test JWTs** and a
  loopback-only console. Real residents and actual operator identity/permissions
  are a separate sign-off, not provided by this change.
- The standard WSL2 watcher executes .NET restore/build/smoke only. Dedicated
  real disposable PostgreSQL acceptance, browser/Vite, real Ollama and
  dedicated Telegram test bot all require separate explicit verification.
- TradeOps-v2 host tests retain priority; no shared Docker cleanup, WSL
  shutdown, global PATH, OS process, service or PowerShell task is touched.

