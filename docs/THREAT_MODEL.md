# Threat model — ResidentChat

Status: initial design, synthetic-only RC-01. Reassess before pilot or new integrations.

| Threat | Boundary | Mandatory mitigation |
|---|---|---|
| Forged webhook | Public ingress | Secret header; timing-safe compare; explicit disabled-by-default configuration |
| Payload exhaustion | Ingress | Small hard byte limit; JSON depth bound; early rejection |
| Chat impersonation / unauthorized group | Ingress | Allowlist numerical Telegram chat IDs; require group/supergroup message |
| Telegram retry/replay | Ingress | Bounded dedup; durable idempotency required before any side effects |
| Sensitive data leakage to cloud AI | Inference | No third-party AI SDKs/endpoints; no fallback; network egress controls |
| Sensitive data in logs | Application/CI | Never log request body, identifiers or tokens; synthetic test data only |
| Prompt injection | Local inference/actions | Text is untrusted; model proposes, C# policy authorizes |
| Cross-building data access | Storage/admin | Tenant-scoped auth and data access, tested before persistence |
| Database/backups compromise | Storage | Minimize data; encryption/access control; retention/deletion design |
| Human mishandling and incorrect classification | Operations | Review queue, urgency routing, operator override, audit |
| Telegram as third party | Messaging platform | Transparent notice to residents; do not claim total on-prem custody |
| Software supply-chain or CI exposure | Build | Pin dependencies, avoid live secrets/test transcripts, review actions |

RC-01 intentionally ingests only synthetic updates, does not persist chat text, does not send messages and does not call any model. In-memory replay cache is a temporary safeguard, not a production guarantee of exactly-once effects.

**Before production:** Verify reverse-proxy TLS, webhook source hygiene, egress deny policy, secret rotation, data-processing basis, retention, audit controls, safe disaster recovery, and customer authorization.