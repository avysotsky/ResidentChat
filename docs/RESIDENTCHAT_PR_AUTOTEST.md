# ResidentChat autonomous PR autotest

The WSL2 watcher is deterministic and uses no LLM. It watches only
`avysotsky/ResidentChat` pull-request HEAD refs and runs every new SHA once.

Fixed pipeline:

1. `dotnet restore tests/ResidentChat.SmokeTests/ResidentChat.SmokeTests.csproj`
2. `dotnet build ... -c Release --no-restore`
3. `dotnet run ... -c Release --no-restore --no-build`

The third stage is the repository's synthetic security gate. It must not use
real Telegram credentials, real resident messages, hosted LLM/OCR, analytics,
or any production integration.

Results are immutable JSON files on the `autotest-results` branch of the
private source repository:

`autotest-results/residentchat/pr-<PR_NUMBER>/<FULL_SHA>.json`

The external development agent must commit to a Draft PR, wait about one
minute, then read the result for the exact current HEAD SHA. PASS permits the
next development step. FAIL requires a new corrective commit; do not rerun or
replace a result for the same SHA. Never merge automatically.

Runtime state is separate from JobRadar under
`/home/user/ResidentChatPRAutotest`. Both watchers use
`/home/user/WSLAutotest/execution.lock` so only one Docker test pipeline runs
at a time. Only restore has external network; build and test have no network.
