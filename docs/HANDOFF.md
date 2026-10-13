# ResidentChat — canonical handoff

Updated: 2026-10-13  
Repository: **PRIVATE** `avysotsky/ResidentChat`  
Status: security-first foundation; no production integration authorized.

## Mission and scope

Create a residential-community chat assistant to moderate spam, answer verified FAQs, classify maintenance problems, help register/route requests, and escalate safety/conflict/ambiguous cases to authorized humans. First channel is a *hypothesized* Telegram group, subject to customer confirmation and permission. Initial languages: Ukrainian and Russian. The product is separate from TradeOps, DocFlow and JobRadar.

A vacancy is market research, **not** authorization from a residents' community, management company or employer. No real residents, credentials, contact details, apartment identifiers or transcripts may enter the repository, PRs, CI outputs, external assistants, tests or telemetry.

## Non-negotiable architectural invariants

- **PRIV-001:** No resident conversations or correlated personal information are sent to hosted LLMs, embedding providers, vector databases, external AI monitors, crash collection, or analytics. No paid/free cloud fallback.
- **PRIV-002:** Inference, retrieval, embeddings and logs stay in infrastructure under approved operational control; a rented VPS has a third-party hosting exposure even if inference is local.
- **PRIV-003:** Enforce network egress restrictions independently of application code. Permitted production egress must be enumerated and reviewed. Telegram Bot API is an explicit unavoidable transport integration.
- **PRIV-004:** Reject unauthorized ingress; verify Telegram webhook secret in constant time, use explicit chat allowlist, bound payloads, and reject invalid configuration by default.
- **PRIV-005:** Keep residents' personal data out of ordinary logs, metrics, exception output, git history and prompts unless narrowly necessary. Redaction is *best effort* and cannot establish anonymization.
- **PRIV-006:** Minimize persisted data; decide consent/notice, access permissions, encryption, retention, deletion, backup protection and incident handling *before* real-world deployment.
- **PRIV-007:** Treat chat messages, links, images, attachments and retrieved text as untrusted. Prompt injection never changes permissions or triggers arbitrary tools.
- **PRIV-008:** Authorize all operations in deterministic C# code; local model outputs are advisory and schema-validated.
- **PRIV-009:** No autonomous bans, critical incident closure, personal-data disclosure, fabricated repair promises or irreversible actions. Escalate urgent, disputed, uncertain cases to humans.
- **PRIV-010:** No unauthorized history scraping. Telegram itself processes group messages; never claim that no third party ever sees them.
- **PRIV-011:** Secrets must be injected at runtime, rotated and excluded from source/logs; never print webhook tokens or raw payloads.
- **PRIV-012:** Tests and demos use synthetic messages only. Security gates must include HTTP-level verification and infrastructure/network validation.

## Architectural sketch

```text
Telegram group (external platform)
  -> TLS Telegram webhook (secret header + max size)
  -> .NET 10 HTTP ingress (allowlisted chat + replay protection)
  -> deterministic C# rules and safety checks
  -> optional isolated local-only Qwen/llama.cpp/Ollama adapter
  -> validated structured recommendation
  -> human-approval policy for consequential actions
  -> authorized reply, maintenance ticket or operator escalation
  -> PostgreSQL (minimal agreed metadata and tickets)
```

No generic cloud model client, auto-network retrieval or outbound analytics. Knowledge must come from customer-approved documentation; retrieval stays local. Unknown answers or model failures route to humans rather than an external AI provider.

## Milestones

### MVP-0 — Private baseline
- Private repository, canonical handoff and threat model.
- .NET 10 foundation and synthetic-only build/tests.
- Explicit disabled-by-default integrations and no live credentials.

### RC-01 — Secure Telegram inbound slice (FIRST)
- Webhook endpoint with `X-Telegram-Bot-Api-Secret-Token`, strong injected secret, timing-safe comparison.
- Strict configurable chat allowlist, group/supergroup constraints and bounded request body.
- Strict `update_id` validation, duplicate protection and safe behavior for invalid/foreign/unsupported updates.
- No Telegram outgoing Bot API, no model calls, no automatic decisions; **zero raw-payload logging or persistence**.
- Unit/synthetic tests covering invalid secrets, excessive payload, malformed update, allowlist, duplicates, disabled configuration; HTTP gate where feasible.
- Must document that in-memory deduplication is not durable and is inadequate for future non-idempotent side effects.

### RC-02 — Authorized operator workflow
- Explicit admin authentication/authorization, cross-building isolation, maintenance categories, human review.
- Define minimal persistence and retention model before storing actual tickets.
- Every important change logged only through privacy-approved structured audit events.

### RC-03 — Local AI
- Isolated local model adapter with no remote fallback.
- Strict JSON schema and bounded prompts, untrusted-text handling and redaction.
- Ukrainian/Russian evaluation and measured CPU/GPU latency using synthetic cases.
- Local knowledge base (initially PostgreSQL full-text).

### RC-04 — Controlled pilot
- Independent egress audit, operator runbooks, backups and retention, review of client permissions.
- Authorized trial with quality/accuracy/safety/efficiency measurement. Prior 70–90% automation estimates are hypotheses, not measured claims.

## RC-01 acceptance contract

1. No HTTP endpoint can ingest residents' messages until enabled explicitly and correctly configured.
2. Missing, invalid or weak webhook secret fails closed before reading request body.
3. Only approved Telegram group IDs are considered; unsupported types and unapproved chats do not enter processing.
4. Maximum input bytes and JSON depth enforced; invalid or excessive data does not produce unhandled exception or content logs.
5. Duplicated update IDs never enter the temporary in-memory processing twice within the accepted window; no claim of cross-restart idempotence.
6. No real chats, Telegram sendMessage calls, or third-party AI inference.
7. CI passes using synthetic data; build on .NET 10. Evidence must distinguish actual tests from code review.

## Ownership and coordination

Development is PR-based. Keep `main` stable; create small reviewable Draft PRs, without merging automatically. Handoff and source code in private GitHub are authoritative. Avoid manual owner relay loops; routine CI results should be read from the PR. If a local tester is later integrated, use project-specific queue and no resident data.

## Operational blockers before any real deployment

Customer must confirm group platform, own the chat, authorize the bot, approve privacy/retention rules and escalation contacts. Source repo is not permission to process historical messages. Administrator must configure HTTPS, token, permitted chat IDs and actual egress firewall.

## Next-agent instructions

Read this file and `docs/THREAT_MODEL.md` first; inspect current `main` and Draft PRs before writing. Do not weaken privacy invariants to simplify tests. Never send resident content to external models, even for debugging. Build/test synthetic-only. Do not claim code is compiled unless real build logs exist.