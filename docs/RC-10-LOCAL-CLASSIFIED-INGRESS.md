# RC-10 — bounded local-Qwen-to-PostgreSQL classification gate

**Draft, source-only; NO real Telegram account, resident or production activation.**
Stacked on React PR #14. The original synthetic and metadata-only modes remain
unchanged by default.

## Explicit test-only activation

To enable the NEW classified maintenance slice, all previous lab settings
(authorized negative Telegram group IDs, tenant-mapped `DurableIntake`,
`DurableProposals:Mode=synthetic-postgres-only` and signed operator/JWT)
must be valid. Additionally:

- `ResidentChat:ClassifiedTelegram:Enabled=true`
- `ResidentChat:ClassifiedTelegram:Mode=maintenance-only-test-groups`
- `ResidentChat:ClassifiedTelegram:OllamaEndpoint=http://127.0.0.1:11434/api/generate`
- `ResidentChat:ClassifiedTelegram:Model=qwen3:1.7b`

All keys are disabled/absent by default. Only `127.0.0.1` or `::1`
absolute /api/generate loopback is permitted, using a proxy-free HTTP client
with redirects disabled. No hosted LLM, external embeddings, telemetry,
model-to-database access or model-issued operator actions.

## Transaction/ACK boundary

1. The trusted Telegram admission policy checks the webhook secret and
   allowlisted group; long polling also uses the same trusted group IDs.
2. For an allowed text update, a **single-flight** C# pipeline passes only
   the transient text to local Qwen with a 15-second budget, applying the
   existing independent C# urgency/injection floor.
3. Only valid classified maintenance proposals from `DemoDispatchPolicy`
   qualify; every proposal requires subsequent **human approval**.
   Emergency/conflict/FAQ/spam/ambiguous/failed/timeout/non-text inputs are
   NOT acknowledged by this maintenance-only test mode. The webhook returns
   503 or getUpdates holds the offset; no lost unclassified message is
   falsely marked processed.
4. `PostgresTelegramIntakeStore.AcceptWithProposalAsync` inserts BOTH
   the RLS-scoped Telegram intake metadata and RLS-scoped pending proposal
   in **one PostgreSQL transaction**. No message text is saved. Duplicate
   update IDs are recognized only if a matching previously committed
   proposal and HMAC group fingerprint exist. A previous metadata-only
   intake without any proposal is not incorrectly treated as complete.
5. Only after commit does the webhook return 200 / getUpdates advance
   its in-memory offset. Unknown/ambiguous commit is not retried as a
   new operator action. Existing read-only React proposal list can display
   the pending item and the operator must sign and explicitly APPROVE/REJECT;
   ticket creation remains a separate audit-logged transaction.

## Critical limitations

- This is **not** a reliable all-message Telegram consumer. Nonmaintenance
  input cannot be safely acknowledged until a proper human-review queue is
  implemented; continuous retry can block polling. Telegram has its own
  finite update-retention period, so an extended offline or stuck model
  **can still lose updates after Telegram expires them**. Do not present this
  experiment as lossless production ingestion.
- No permanent encrypted content queue, reviewable message summary or safe
  retention/redaction policy exists yet. No real residents or real building
  groups may be connected to this classified test mode.
- The model adapter is the existing local Qwen integration; a guarded
  classifier result is not proof of raw-model reliability.
- Classification uses single-flight gating to reduce interference with
  TradeOps-v2. No new service, port, Docker process, PowerShell command or
  Ollama instance is started by these commits. The existing WSL2 auto-watch
  only executes its ordinary .NET smoke after a Draft PR commit.
- The independent real PostgreSQL operator acceptance has been extended
  with a **synthetic C# classification result** and tests atomic intake +
  proposal commit/duplicate/RLS. It has not been observed running on the
  owner's disposable PostgreSQL yet. Normal WSL2 .NET PASS does not cover
  PostgreSQL, live Ollama, React Vite build or browser.
