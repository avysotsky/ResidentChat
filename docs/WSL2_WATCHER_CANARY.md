# ResidentChat WSL2 watcher canary

This is a disposable, **synthetic-only** diagnostic PR.

- Base is the last confirmed passing RC-02 Draft PR branch.
- No application code, secrets, residents, Telegram group identifiers, Ollama calls, or GitHub Actions configuration changes.
- Exactly the normal WSL2 watcher should run the fixed `tests/ResidentChat.SmokeTests/ResidentChat.SmokeTests.csproj` pipeline.
- An immutable `autotest-results/residentchat/pr-<PR>/<SHA>.json` for the exact HEAD distinguishes a working new-PR discovery path from a stopped/broken watcher.
- Do not merge this PR.
