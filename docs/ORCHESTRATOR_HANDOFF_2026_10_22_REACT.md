# ResidentChat — React handoff — 2026-10-22

Private repository: `avysotsky/ResidentChat`. The owner approved **React + TypeScript + Vite** for the dispatcher UI, instead of Angular or Blazor.

## Read first
- `docs/HANDOFF.md` — canonical privacy and safety invariants (the old milestone status is out of date).
- `docs/RC-09-REACT-FRONTEND-ADR.md` — architecture decision and frontend acceptance criteria.
- `docs/RC-06-OPERATOR-CONSOLE-LAB.md`, `docs/RC-07-WINDOWS-BROWSER-LAB.md`, `docs/RC-08-WATER-LEAK-SAFETY-FLOOR.md`, `docs/THREAT_MODEL.md`.

## PR stack: all Draft and unmerged
#1 ingress; #2 JWT, tenants, PostgreSQL RLS; #3 isolated Qwen; #8 approved FAQ lookup; #9 synthetic demo; #10 explicit operator approval and disposable PostgreSQL; #11 vanilla JS console; #12 Windows browser lab; #13 independent C# safety floor for active common-area water leaks; RC-09 documentation branch builds upon #13. Confirm current branch and PR numbers from GitHub before proceeding.

The exact RC-08 PR #13 SHA `d7ddf086dfa48834f41654c5fe515006e9514810` has **WSL2 .NET restore/build/smoke PASS** and **local guarded-Qwen evaluation PASS 6/6**. Immutable reports are in `autotest-results` branch under the corresponding `pr-13/<SHA>.json` paths. The guarded score reflects Qwen plus C# policy, not raw model accuracy.

## Real owner-observed synthetic browser acceptance
- ALPHA-WATER was approved; the ticket status API returned **Escalated (version 1)**.
- BETA-ELEVATOR was rejected; UI reported no ticket write.
- The later feature that displays ticket status inline was added after that browser lab launch, so its visual behavior is not yet manually confirmed.

## Next deliverable: RC-09 real React implementation
Implement a tested `frontend/` React + TypeScript + Vite application, replacing the existing *synthetic* HTML/CSS/JS operator console. Use the present .NET operator API, tenant-scoped signed-JWT queue, explicit approve/reject confirmation, and ticket status/version readback. Handle authorization failures, duplicate writes and ambiguous commits without automatic POST retries. Keep temporary lab JWT only in browser memory; no localStorage or telemetry, no external CDN. Bundle and serve static assets same-origin with strict localhost/CSP restrictions. Avoid new cross-origin/CORS permissions.

Preserve PostgreSQL RLS, deterministic authorization and human approval. Do not connect real Telegram, residents, hosted AI, or production data. The existing API and local Qwen remain separate; browser never calls Ollama.

WSL2 `user` has an existing Git mirror and automatic Draft-PR watcher. Each new SHA must have an observed WSL2 report (and model test where appropriate). GitHub Actions, auto-merge, deployment and production connections are not authorized. The assistant cannot run Docker on the user's computer; never claim tests executed without results.

Next-chat command:
“Продовжуй ResidentChat у приватному GitHub. Прочитай `docs/ORCHESTRATOR_HANDOFF_2026_10_22_REACT.md` і `docs/RC-09-REACT-FRONTEND-ADR.md`. Почни реалізацію React + TypeScript + Vite з перевіркою автономних SHA-scoped тестів. Тільки синтетичні дані; без Telegram і merge.”
