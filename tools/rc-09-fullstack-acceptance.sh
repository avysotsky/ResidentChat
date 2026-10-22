#!/usr/bin/env bash
# RC-09: one-shot actual synthetic acceptance pinned to exact SHA.
# Real Kestrel + disposable PostgreSQL17 + independent PostgreSQL/RLS suite.
# Vite gate only if offline lock/dependencies preexist. Never merge/deploy.
set -Eeuo pipefail
umask 077

if [[ $# != 1 || ! "$1" =~ ^[0-9a-f]{40}$ || "$(id -u)" == 0 ]]; then
    echo 'RC-09 NOT_RUN: supply exact SHA as unprivileged WSL user'
    exit 2
fi
sha="$1"
mirror=/home/user/ResidentChatPRAutotest/source.git
[[ -d "$mirror" ]] || { echo 'RC-09 NOT_RUN: private WSL source mirror missing'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo 'RC-09 NOT_RUN: exact SHA not fetched by WSL watcher'
    exit 2
}
work="$(mktemp -d /tmp/rc09-fullstack.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT

# Read the tested scripts from the exact commit, never current main.
for name in rc-07-local-browser.sh rc-05-approved-ticket-pg-acceptance.sh rc-09-react-offline-gate.sh; do
    git --git-dir="$mirror" show "$sha:tools/$name" > "$work/$name"
    bash -n "$work/$name" || {
        echo "RC-09 FAIL: $name Bash syntax gate" >&2
        exit 1
    }
done
echo "RC-09 ACTUAL FULLSTACK SHA: $sha"
echo 'RC-09 STEP 1: actual loopback Kestrel + disposable PostgreSQL 17'
bash "$work/rc-07-local-browser.sh" "$sha" --pg-acceptance
echo 'RC-09 STEP 2: independent JWT, SQL role, RLS, audit and version tests'
bash "$work/rc-05-approved-ticket-pg-acceptance.sh" "$sha"
echo 'RC-09 LIVE API + POSTGRESQL ACCEPTANCE PASS'

# Networkless React/npm gate. Missing lockfile means NOT_RUN, not PASS.
if git --git-dir="$mirror" ls-tree -r --name-only "$sha" |
   grep -Fxq frontend/package-lock.json; then
    if docker image inspect node:22-bookworm >/dev/null 2>&1 &&
       { [[ -d /home/user/ResidentChatPRAutotest/npm-cache/_cacache ]] ||
         [[ -d /home/user/.npm/_cacache ]]; }; then
        echo 'RC-09 STEP 3: offline React tests, Vite assets and .NET package'
        bash "$work/rc-09-react-offline-gate.sh" "$sha"
    else
        echo 'RC-09 REACT NOT_RUN: pinned Node image or npm cache unavailable'
    fi
else
    echo 'RC-09 REACT NOT_RUN: reviewed package-lock.json absent from exact SHA'
fi
echo "RC-09 COMPLETED sha=$sha: PostgreSQL PASS; React result above"
echo 'RC-09 LIMIT: browser automation/Qwen not exercised by this script'
