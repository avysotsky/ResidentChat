# RC-08 — independent safety floor for active shared-area water leaks

**Status:** Draft, synthetic-only. Branch stacks on RC-07 PR #12; no real Telegram, operator PII, resident text, production database, internet LLM or deployment.

## Risk and observed regression

RC-07's immutable Qwen3 evaluation reported **5/6** for the Russian synthetic fixture:
`Тест: на лестничной площадке течёт вода.` The small local model returned Maintenance/Water but incorrectly labelled the incident **Routine**, `requires_human=false` instead of **Urgent**, `requires_human=true`. The same fixture passed on other runs, so this is not a deterministic model contract.

Prompt-only improvements cannot guarantee reliable incident triage. RC-08 adds a deliberately narrow, independent **C# post-classification policy** that prevents a low model urgency from suppressing a known active water leak in a shared building space.

## Change

`LocalModelClassifier.ApplyIndependentSafetyFloor` now requires **all three** separate textual cues before adding the water-leak escalation:

1. **Shared area**, e.g. common stairwell/landing, building entrance, basement, corridor or English common-area variants;
2. **Water** marker in Russian, Ukrainian or English;
3. **Active leak/flooding** marker in the relevant language.

For such messages, the final `ResidentDecision` has at least `ResidentUrgency.Urgent` and `RequiresHuman=true`, regardless of a valid but unsafe model recommendation. Its `Valid`, `Intent`, and `Category` remain **unchanged**; a lexical heuristic is not trusted to reinterpret the complete message or make an authorization decision. No automatic write occurs: approvals still require operator authentication, tenant scope, exact human action and PostgreSQL RLS.

The `DemoDispatchPolicy` routes urgent human-reviewed non-Maintenance decisions to `OperatorReview` rather than `ApprovedFaqRequired` or `ModerationReview`. This avoids hiding active hazards when Qwen mislabels the intent as FAQ or spam; it never grants additional capabilities to the model.

The local Qwen system instructions additionally state that an active leak/flood in a common building space is Urgent and human-reviewed. The deterministic C# policy, **not this prompt wording**, enforces the floor.

## Synthetic regression coverage

Offline smoke fixtures provide a fake Ollama handler that **deliberately returns an incorrect Routine/no-human JSON**. Assertions cover active leaks in Russian (including `ё/е`), Ukrainian and English, basements and stairwells; verify Urgent/human and metadata-only, uncommitted proposal routing; check misleading model FAQ/Spam intents result in a manual operator queue; preserve non-escalation of unrelated water outages, apartment faucets, ordinary light outages and schedules; and retain invalid output as invalid with an urgent manual review and **no** ticket proposal.

The live local Qwen3 evaluation still has six fictional cases and can exercise `ClassifyAsync` end-to-end. **Its score is for the guarded classifier (Qwen + deterministic safety policy), not raw Qwen accuracy**, and six fixtures cannot establish production recall or acceptable false-positive rates. See `tools/ResidentChat.LocalLab/Program.cs`.

## Unresolved safety issues / boundaries

- Keyword cues are heuristic and language-limited; paraphrases, speech recognition errors, missing place names, misleading negation, quoting, or typos can cause both false negatives and false positives. A human must assess consequences.
- More dangerous water hazards (near electrical panels, burst pressurized lines, structural risk) may deserve Emergency treatment; this narrow change sets **at least Urgent**, not a universal emergency classification.
- No production incident response SLA, resident notice, escalation roster, on-call reliability, resilient durable inbox, independent audit of human decisions, rate limiting, retention/consent policy, deployment secrets or verified egress policy.
- The RC-06 and RC-07 browser demos still create **fictional** tickets from fixed synthetic proposals, not from LLM suggestions; do not claim this new policy has been validated in a live browser flow.

## Acceptance gates

- Immutable WSL2 SHA-specific restore/build/smoke report.
- Separate SHA-specific local Qwen3 synthetic fixture evaluation.
- If any fail, diagnose and correct before calling this slice accepted. No merging/deployment regardless of PASS until independent security review and owner authorization.
