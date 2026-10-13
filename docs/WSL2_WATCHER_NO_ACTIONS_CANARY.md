# WSL2 discovery canary

This is a disposable diagnostic Draft PR based on `main`, containing **no** .github/workflows directory or hosted LLM integration. It tests whether the WSL2 autonomous watcher discovers an ordinary PR HEAD without a `[skip ci]` marker. This commit does **not** invoke GitHub Actions.

The synthetic-only smoke program neither connects to Telegram nor calls Ollama or external networks. It does not represent actual ResidentChat product functionality and must never be merged.

Expected result: `autotest-results/residentchat/pr-5/<FULL_SHA>.json` on `autotest-results` branch. Compare with diagnostic PR #4, which contains a `[skip ci]` commit. If PR #5 gets a JSON result while #4 does not, the difference is worth investigating in the WSL watcher implementation. Do not assert cause from one observation.
