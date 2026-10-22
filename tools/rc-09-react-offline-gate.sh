#!/usr/bin/env bash
# RC-09: strict SHA-pinned offline React + .NET packaging gate.
# No GitHub Actions, network, Telegram, residents, hosted AI, production.
set -Eeuo pipefail
umask 077

if [[ $# != 1 || ! "${1:-}" =~ ^[a-f0-9]{40}$ || "$(id -u)" == 0 ]]; then
    echo "RC-09 NOT_RUN: exact commit SHA required; run as ordinary WSL user"
    exit 2
fi
sha="$1"
mirror=/home/user/ResidentChatPRAutotest/source.git
nuget=/home/user/ResidentChatPRAutotest/nuget
lock=/home/user/WSLAutotest/execution.lock
node_image=node:22-bookworm
dotnet_image=jobradar-pr-autotest:dotnet10

for cmd in git docker tar flock mktemp; do
    command -v "$cmd" >/dev/null || {
        echo "RC-09 NOT_RUN: missing local utility $cmd"
        exit 2
    }
done
[[ -d "$mirror" && -f "$lock" ]] || {
    echo "RC-09 NOT_RUN: owner WSL2 source mirror/runner lock absent"
    exit 2
}
exec 9<>"$lock"
flock -w 120 9 || {
    echo "RC-09 NOT_RUN: shared watcher lock busy"
    exit 2
}
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo "RC-09 NOT_RUN: exact source SHA absent from mirror"
    exit 2
}
docker image inspect "$node_image" >/dev/null || {
    echo "RC-09 NOT_RUN: offline Node 22 container image not cached"
    exit 2
}
docker image inspect "$dotnet_image" >/dev/null || {
    echo "RC-09 NOT_RUN: offline .NET 10 SDK image not cached"
    exit 2
}
if [[ -d "$nuget/packages/npgsql" ]]; then
    packages=/nuget/packages
elif [[ -d "$nuget/npgsql" ]]; then
    packages=/nuget
else
    echo "RC-09 NOT_RUN: offline Npgsql dependencies unavailable"
    exit 2
fi

# The only npm source is an existing local offline cache, never the registry.
if [[ -d /home/user/ResidentChatPRAutotest/npm-cache ]]; then
    npm_cache=/home/user/ResidentChatPRAutotest/npm-cache
elif [[ -d /home/user/.npm/_cacache ]]; then
    npm_cache=/home/user/.npm
else
    echo "RC-09 NOT_RUN: offline npm cache is not provisioned"
    exit 2
fi
work="$(mktemp -d /tmp/residentchat-rc09.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -m 700 "$work/src" "$work/npm-cache"
# Copy only npm content-addressed tarballs/metadata; never mount ~/.npmrc,
# auth tokens, private per-user npm config or the live cache in containers.
[[ -d "$npm_cache/_cacache" ]] || {
    echo "RC-09 NOT_RUN: offline npm content-addressed cache is absent"
    exit 2
}
cp -a "$npm_cache/_cacache" "$work/npm-cache/_cacache"
chmod -R u+rwX "$work/npm-cache"

git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/src"
if [[ ! -f "$work/src/frontend/package-lock.json" ]]; then
    echo "RC-09 NOT_RUN: reviewed reproducible frontend/package-lock.json absent at pinned SHA"
    exit 2
fi
[[ -f "$work/src/frontend/tests/bundle-check.mjs" ]] || {
    echo "RC-09 NOT_RUN: pinned React bundle test is missing"
    exit 2
}
echo "RC-09 pinned frontend source: $sha"

# npm ci with lifecycle scripts disabled; network=none even if npm is
# accidentally reconfigured. No bind mounts exposing real credentials.
docker run --rm --pull never --network none --read-only \
    --cpus 2 --memory 1536m --memory-swap 1536m --pids-limit 128 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work/frontend \
    --tmpfs /tmp:rw,nosuid,nodev,size=256m \
    --mount "type=bind,src=$work/src,dst=/work" \
    --mount "type=bind,src=$work/npm-cache,dst=/npm-cache" \
    -e npm_config_cache=/npm-cache \
    -e npm_config_logs_dir=/tmp \
    -e npm_config_offline=true \
    -e npm_config_audit=false \
    -e npm_config_fund=false \
    -e npm_config_update_notifier=false \
    -e NODE_ENV=development \
    "$node_image" /bin/bash -c '
        set -Eeuo pipefail
        npm ci --offline --ignore-scripts --no-audit --no-fund
        npm run test
        npm run build
        npm run verify:bundle
    '
echo "RC-09 OFFLINE NODE TESTS / TYPESCRIPT / VITE BUNDLE PASS"

# Package exactly the generated resources alongside .NET without running
# npm during dotnet restore/build and without enabling a network connection.
docker run --rm --pull never --network none --read-only \
    --cpus 2 --memory 1g --memory-swap 1g --pids-limit 128 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$work/src,dst=/work" \
    --mount "type=bind,src=$nuget,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$packages" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    "$dotnet_image" /bin/bash -c '
        set -Eeuo pipefail
        dotnet restore src/ResidentChat.Api/ResidentChat.Api.csproj \
            --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
        dotnet publish src/ResidentChat.Api/ResidentChat.Api.csproj \
            -c Release --no-restore -o /work/.rc09-api -v quiet
    '
test -s "$work/src/.rc09-api/operator-ui/index.html" || {
    echo "RC-09 FAIL: bundled React index missing from .NET publish"
    exit 1
}
compgen -G "$work/src/.rc09-api/operator-ui/assets/index-*.js" >/dev/null || {
    echo "RC-09 FAIL: bundled React JS missing from .NET publish"
    exit 1
}
compgen -G "$work/src/.rc09-api/operator-ui/assets/index-*.css" >/dev/null || {
    echo "RC-09 FAIL: bundled React CSS missing from .NET publish"
    exit 1
}
echo "RC-09 FRONTEND+DOTNET PACKAGE PASS sha=$sha"
echo "RC-09 LIMIT: no HTTP/real PostgreSQL/browser/Qwen acceptance claimed"
