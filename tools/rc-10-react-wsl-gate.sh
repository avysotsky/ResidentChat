#!/usr/bin/env bash
# RC-10: one-shot reproducible React gate, run by ordinary WSL user.
# No Docker, installed software, Git push, resident messages, or live services.
set -Eeuo pipefail
umask 077
sha="${1:-}"
if [[ $# != 1 || ! "$sha" =~ ^[a-f0-9]{40}$ || "$(id -u)" == 0 ]]; then
  echo 'RC-10 NOT_RUN: supply one exact commit SHA as ordinary WSL user'
  exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
lock=/home/user/WSLAutotest/execution.lock
artifacts=/home/user/ResidentChatPRAutotest/artifacts
for cmd in git node npm tar flock mktemp timeout; do
  command -v "$cmd" >/dev/null || { echo "RC-10 NOT_RUN: missing $cmd"; exit 2; }
done
[[ -d "$mirror" && -f "$lock" ]] || {
  echo 'RC-10 NOT_RUN: WSL watcher mirror or shared lock not found'
  exit 2
}
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
  echo 'RC-10 NOT_RUN: exact SHA is not yet present in WSL watcher mirror'
  exit 2
}
node -e '
const [major,minor] = process.versions.node.split(".").map(Number);
if (!((major === 22 && minor >= 12) || major === 24)) process.exit(2);
' || { echo 'RC-10 NOT_RUN: requires Node 22.12+ or 24.x'; exit 2; }

echo "RC-10 NODE: $(node --version); npm $(npm --version)"
echo 'RC-10 waiting for shared WSL test lock (up to 120 seconds)'
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC-10 NOT_RUN: shared lock occupied'; exit 2; }
echo 'RC-10 shared lock acquired'

work="$(mktemp -d /tmp/rc10-react.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -m 700 "$work/src" "$work/npm-cache"
git --git-dir="$mirror" archive "$sha" frontend | tar -xf - -C "$work/src"
cd "$work/src/frontend"
[[ -f package.json && -f tests/bundle-check.mjs ]] || {
  echo 'RC-10 NOT_RUN: pinned React source incomplete'
  exit 2
}

# Public npm registry is the only external destination.
# Read no per-user npmrc or credentials; no install scripts, audits or telemetry.
unset NPM_TOKEN NODE_AUTH_TOKEN npm_config__authToken
export npm_config_cache="$work/npm-cache"
export npm_config_userconfig=/dev/null
export npm_config_globalconfig=/dev/null
export npm_config_registry=https://registry.npmjs.org/
export npm_config_audit=false
export npm_config_fund=false
export npm_config_update_notifier=false
export npm_config_ignore_scripts=true
export npm_config_progress=false
export NODE_OPTIONS=--max-old-space-size=512
export NODE_ENV=development

if [[ ! -f package-lock.json ]]; then
  echo 'RC-10 STEP 1: generate public dependency lockfile'
  timeout 240 npm install --package-lock-only --no-audit --no-fund \
    --ignore-scripts --fetch-retries=1 --fetch-timeout=15000
fi
echo 'RC-10 STEP 2: install dependencies pinned by lockfile'
timeout 240 npm ci --ignore-scripts --no-audit --no-fund \
  --fetch-retries=1 --fetch-timeout=15000
echo 'RC-10 STEP 3: verify reproducibility without network'
timeout 240 npm ci --offline --ignore-scripts --no-audit --no-fund
echo 'RC-10 STEP 4: contract tests, TypeScript, Vite and bundle audit'
timeout 240 npm run check
timeout 60 npm run verify:bundle
echo 'RC-10 REACT/NPM/VITE PASS'

# Export only lockfile, never user credentials, source text, or node_modules.
mkdir -p -m 700 "$artifacts"
output="$artifacts/rc10-$(printf '%s' "$sha" | cut -c1-12)-package-lock.json"
cp -- package-lock.json "$output"
chmod 600 "$output"
echo "RC-10 LOCKFILE_READY: $output"
echo 'RC-10 NEXT: attach that package-lock.json to this conversation'
echo 'RC-10 LIMIT: browser, .NET packaging and live Telegram are not exercised'
