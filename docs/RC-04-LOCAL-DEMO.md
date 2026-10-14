# RC-04 — visible, synthetic local operator demonstration

**Status:** Draft, non-production, stacked on RC-03b PR #8, which stacks on #3, #2, #1. No merges, real Telegram, real residents, cloud LLMs, live ticket writes, deployments or schema changes.

## What the owner can now see

A single terminal run processes six **hard-coded, fictional** RU/UA/English examples through:

1. **Input:** a fictional tenant label and synthetic case ID (no Telegram group, user identity or chat history).
2. **Classifier:** either a clearly marked **scripted offline fixture** or **real local Qwen3** at `127.0.0.1:11434` using the already-reviewed RC-03 C# adapter.
3. **Deterministic C# authorization boundary:** `DemoDispatchPolicy.Plan` validates the enum decision and trusted tenant ID, prohibits unauthorized model action, maps only recognized maintenance categories to an *uncommitted* metadata-only ticket proposal, and forces explicit human approval.
4. **Operator view:** one-terminal-screen proposal list and review list. Only typed case identifiers, categories, priorities and queue status are printed; no message text is printed.

**Important:** “TicketProposal” is NOT a database row or actual ticket; the operator view is an in-memory simulator, NOT an authenticated UI or the existing HTTP operator API. Real PostgreSQL ticket writes and real Telegram integration are intentionally absent, so the demo cannot accidentally process a live resident message or cause automated moderation. There is no external network access in the offline variant.

## Quick run (already-installed WSL2 Docker image)

From PowerShell, fetch the exact PR HEAD into the existing ResidentChat WSL2 Git mirror and invoke `tools/rc-04-demo-offline.sh` at that SHA. The script uses the shared `execution.lock`, local offline NuGet cache and SDK Docker image `jobradar-pr-autotest:dotnet10`, enforces `--network none`, and removes its temporary source tree.

Example execution *from within WSL2 after fetching the branch*:

```bash
sha="$(git --git-dir=/home/user/ResidentChatPRAutotest/source.git rev-parse refs/heads/ResidentChat/rc-04-synthetic-operator-demo)"
git --git-dir=/home/user/ResidentChatPRAutotest/source.git show "$sha:tools/rc-04-demo-offline.sh" | bash -s -- "$sha"
```

Expected labels: `RESIDENTCHAT — SYNTHETIC LOCAL DEMO`, `Proposed tickets`, `Other reviews`, `DEMO COMPLETE`, and `RC-04 OFFLINE DEMO PASS`. **Offline mode does not call Qwen and is never reported as an AI accuracy test.**

## Real local Qwen mode — explicitly opt-in, same-host only

On a machine with **.NET 10 SDK** and the locally installed `qwen3:1.7b` in Ollama running on `127.0.0.1:11434`:

```powershell
dotnet run -c Release --project tools/ResidentChat.Demo/ResidentChat.Demo.csproj -- --run --model qwen3:1.7b
```

The adapter rejects remote endpoints and exposes no cloud fallback. There is no real Telegram, Postgres write or moderation capability. Model failures are routed to operator review without a proposed ticket. The typed output is *advisory*, not an automated action. Do not pass arbitrary real messages or resident information.

## Review and regression boundary

- The WSL2 `ResidentChat.SmokeTests` project now exercises deterministic proposal policy for urgent/routine maintenance, tenant control, invalid or malicious categories, emergency/unknown model responses, FAQ and spam. It also references the demo console project for **compile-only** coverage; it does NOT run Ollama.
- The prior RC-03 real-Qwen six-scenario model evaluator is independent of this demo and must retain its separate per-SHA evidence.
- PR #8 real PostgreSQL FAQ acceptance PASS was manually observed at parent SHA `3cc7ad9996c9c70a884cbc9ace51cdc23f0e94d1`. This demo **does not repeat or replace** that gate.
- Any real operator view must be implemented behind authenticated identities, approved retention, tenant-scoped PostgreSQL permissions, manual workflow, and durable ingestion idempotence. All remain separate deployment blockers.

## Next useful step

Once the offline demo compiles and runs, design an **explicit human-approved** local bridge to the existing operator API using synthetic data and a disposable PostgreSQL database. Do not wire Telegram-to-model-to-persistence directly or claim production readiness based on this demonstration.
