# ResidentChat

Private, security-first C#/.NET 10 project for assisted moderation and maintenance-request intake in residents' Telegram groups.

**Non-negotiable:** Resident conversations must never be sent to external AI/LLM, embeddings, vector-database, analytics or telemetry services. Local inference only; no cloud fallback. Telegram remains the transport provider and therefore still processes Telegram messages.

## Current state
- Project specification: [docs/HANDOFF.md](docs/HANDOFF.md)
- Initial implementation: RC-01 (secure Telegram ingress) is to be developed in a separate Draft PR.
- No production chat, tenant, bot credentials, or real residents' messages are connected.
- Synthetic data only in code, tests, issues, CI and pull requests.

## Product scope
Moderation, FAQ assistance, maintenance-request classification and dispatch, and human escalation. Human oversight for conflicts, emergencies and consequential moderation. No automated bans/deletions at the first development stage.

## Technical direction
ASP.NET Core (.NET 10), PostgreSQL when persistence is introduced, deterministic policy engine, optional locally hosted LLM such as Qwen via an isolated loopback inference service. Outbound AI APIs are prohibited.

## Operating rule
Do not enable live ingestion without an approved building administrator, resident-facing notice, privacy/retention policy, and tested security gates.
