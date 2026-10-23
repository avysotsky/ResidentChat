# RC-10 — local Qwen classification with durable encrypted human review

**Draft test-only implementation; no real resident-group or production activation.**
Stacked on the React frontend branch. All live Telegram, model, review and
proposal modes are disabled by default.

## Test-group-only configuration (independent opt-ins)

The existing operator JWT, strict tenant mapping, authorized Telegram group
IDs, durable PostgreSQL intake and durable proposal settings must be valid.
The review queue must also be configured with a locally managed 32-byte
AES-256-GCM key (Base64, **never** in Git, CLI arguments or logs).

- `ResidentChat:ClassifiedTelegram:Enabled=true`
- `ResidentChat:ClassifiedTelegram:Mode=maintenance-and-review-test-groups`
- `ResidentChat:ClassifiedTelegram:OllamaEndpoint=http://127.0.0.1:11434/api/generate`
- `ResidentChat:ClassifiedTelegram:Model=qwen3:1.7b`
- `ResidentChat:TelegramReview:Enabled=true`
- `ResidentChat:TelegramReview:Mode=encrypted-test-groups-only`
- `ResidentChat:TelegramReview:EncryptionKeyBase64=<secret from local environment>`

The permitted Ollama endpoint is loopback `127.0.0.1` or `::1` with the
exact path `/api/generate`; external AI, hosted embeddings, proxies,
redirects and model tool calls are prohibited. All other prerequisite opt-ins
remain required; the properties above **do not** independently activate
Telegram or operator authentication.

## Admission and acknowledgement

1. Strict group allowlisting and tenant mapping are checked before intake.
2. Text of at most 1,500 characters enters single-flight local Qwen
   classification with a 15-second budget and an independent C# safety floor.
   Only suitable maintenance classifications create a **pending proposal**;
   they can never auto-approve a ticket.
3. Nonmaintenance, ambiguous, emergency/conflict, failed-model,
   unsupported/non-text and **1,501–4,096 character** text inputs are routed
   to the tenant-scoped encrypted PostgreSQL human-review queue.
   A model-limit excess must **not** erase the original message.
4. A Telegram update is ACKed only after atomic PostgreSQL intake+proposal
   or intake+encrypted-review commit. Deduplication occurs before local Qwen
   work; intake metadata is bound to the HMAC of the admitted group.
5. Inputs longer than 4,096 characters fail validation: no empty review is
   committed and no Telegram ACK/offset advance occurs. Oversized inputs
   require an explicit later policy rather than silent truncation.
6. React lists encrypted review metadata. An authenticated human must
   explicitly request a single review detail to decrypt it; permitted reads
   are pseudonymously audited. Approval/rejection of ticket proposals is
   a separate signed and audit-logged operator action.

## Important limitations

- PostgreSQL stores encrypted review text transiently with a seven-day
  expiration, **not** a durable lossless archive. An expired review needs
  explicit retention/purge and retry semantics before production readiness.
- Attachment-only messages have no extractable text in this pipeline. A
  non-text review records an empty textual body, not the actual attachment;
  this is incomplete coverage. Do not use with real residents.
- Telegram retains updates for a finite period; connection outages,
  oversized input, expired reviews or repeated processing failures can
  block polling and cause eventual loss. This is **not** lossless delivery.
- Local Qwen classification quality and its 15-second deadline are not
  validated by the .NET synthetic watcher or RC-11 HTTP frontend test.
- All test groups must contain synthetic data only. No Telegram send
  actions, automated emergency handling, production deployment or PR merge
  are enabled.
- Current verified tests: synthetic .NET smoke, RC-05 real PostgreSQL
  operator acceptance on its tested SHA, RC-10 React/Vite build and
  RC-11 real loopback Kestrel serving packaged React assets. Newly added
  long-message regression must obtain its own exact-SHA watcher and
  PostgreSQL acceptance results before being called PASS.
