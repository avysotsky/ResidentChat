# RC-07 — Windows-browser operator lab (WSL2 + Docker, synthetic only)

**Status:** Draft; stacked on RC-06 PR #11 → RC-05 PR #10 → #9 → #8 → #3 → #2 → #1. No merges or real residents.

## What the owner can do

Start a single temporary local lab, open http://localhost:18760/operator/console in Windows, and paste one of the two synthetic JWTs printed to the PowerShell terminal. The ALPHA identity sees ALPHA-WATER, can click Подтвердить and read the resulting metadata-only actual PostgreSQL ticket. The BETA identity sees BETA-ELEVATOR and can click Отклонить, which changes volatile state only, without a database write. A real resident/Telegram group is never involved.

The launcher boots PostgreSQL 17 in a network-disabled, tmpfs-backed Docker container (socket only, no port), runs migrations as the ephemeral database owner, grants least-privilege runtime rights and PostgreSQL FORCE RLS, compiles the existing .NET 10 API and the new self-contained synthetic token issuer offline, generates random signing/audit keys and two distinct tenant-scope 90-minute signed JWTs, and runs a loopback-bound operator API in a temporary Docker container. Source comes only from a pinned Git commit in the existing WSL2 git mirror. All temporary containers and files are removed at exit. Session limit: 75 minutes.

## Usage and gates

- Fetch the RC-07 branch into /home/user/ResidentChatPRAutotest/source.git.
- Materialize tools/rc-07-local-browser.sh at the exact SHA to a UTF-8 LF script, verify with bash -n, and execute from a FILE, never streaming GitHub text through Windows PowerShell to bash -s.
- First use arguments FULL_SHA --self-test to validate the real HTTP page and two tenant-scoped JWT queue requests with no ticket writes, then automatic cleanup.
- For interactive browser use arguments FULL_SHA --run. Keep the PowerShell window open, use the printed alpha or beta JWT, then Ctrl+C to stop.
- Windows PowerShell 5.1 path conversion should transform C:\Users\... to /mnt/c/Users/... explicitly; wslpath through its quoting layer was unreliable in prior RC-05 trials.

Expected selftest labels: RC-07 LOCAL BROWSER GATE PASS, RC-07 SELF-TEST PASS, RC-07 CLEANUP PASS. Browser interaction is NOT claimed verified until the owner tries it.

## Security boundaries

- No real Telegram, resident data, remote LLM, embeddings, hosted inference or telemetry. Telegram ingress is expressly disabled.
- API binds 127.0.0.1:18760; RC-06 console routes check that both socket addresses are loopback and that full signed-JWT operator configuration and a separate synthetic-lab-only feature toggle are valid.
- The PostgreSQL container and all offline build/token tool processes use Docker --network none. PostgreSQL exposes only a Unix socket.
- The API container uses Docker --network host to satisfy the direct-loopback security requirement; this DOES NOT guarantee external egress isolation. Never enter real credentials or resident data; no real deployment allowed. Some Docker Desktop host-network configurations might not work and must fail closed.
- Synthetic keys are freshly generated using openssl rand and stored in a chmod 600 temporary env file until cleanup. The user-visible JWTs are generated for fictional ALPHA/BETA identities and kept only in JavaScript tab memory. No production issuer, MFA, identity accounts, secure session or credential lifecycle exists.
- The source browser view can show only synthetic proposals. Every real database write still requires an explicitly authorized JWT POST and matching case-specific affirmative confirmation. Rejection never writes to PostgreSQL. The process-local proposal queue is not durable and is reset upon restart.
- Do not expose the local port to LAN, tunnel or reverse proxy. Do not merge/deploy without external security review, consent/retention design, durable idempotency, dedicated approval audits, real incident procedures, and firewall verification.

## Evidence

WSL2 restore/build/smoke and separate real local Qwen tests are independent and SHA-scoped, but neither proves the launcher or manual browser works. The standalone --self-test and owner manual browser acceptance are mandatory distinct gates. Mark neither as PASS without actual output.
