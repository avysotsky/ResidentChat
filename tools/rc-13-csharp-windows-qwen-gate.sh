#!/usr/bin/env bash
# RC-13: real C# LocalModelClassifier -> already-installed Windows Qwen.
# Bridge listens ONLY WSL localhost. No Telegram, database, resident data,
# Ollama model download, host configuration changes or Git writes.
set -Eeuo pipefail
umask 077
sha="${1:-}"
if [[ $# != 1 || ! "$sha" =~ ^[0-9a-f]{40}$ || "$(id -u)" == 0 ]]; then
    echo 'RC-13 NOT_RUN: exact commit SHA required as non-root WSL user'
    exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
cache=/home/user/ResidentChatPRAutotest/nuget
lock=/home/user/WSLAutotest/execution.lock
sdk=jobradar-pr-autotest:dotnet10
for cmd in git docker python3 curl tar flock mktemp timeout; do
    command -v "$cmd" >/dev/null || { echo "RC-13 NOT_RUN: missing $cmd"; exit 2; }
done
[[ -d "$mirror" && -f "$lock" ]] || {
    echo 'RC-13 NOT_RUN: WSL watcher mirror or test lock absent'
    exit 2
}
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo 'RC-13 NOT_RUN: exact commit not synchronized in WSL mirror'
    exit 2
}
docker image inspect "$sdk" >/dev/null 2>&1 || {
    echo 'RC-13 NOT_RUN: offline .NET 10 SDK image unavailable'
    exit 2
}
if [[ -d "$cache/packages/npgsql" ]]; then
    packages=/nuget/packages
elif [[ -d "$cache/npgsql" ]]; then
    packages=/nuget
else
    echo 'RC-13 NOT_RUN: offline NuGet cache absent'
    exit 2
fi
echo 'RC-13 waiting for WSL shared test lock (up to 120 seconds)'
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC-13 NOT_RUN: shared test lock busy'; exit 2; }

work="$(mktemp -d /tmp/rc13-csharp-qwen.XXXXXXXX)"
src="$work/source"
bridgepid=""
owns_model=0
cleanup() {
    result=$?
    trap - EXIT INT TERM
    if [[ -n "$bridgepid" ]]; then
        kill "$bridgepid" >/dev/null 2>&1 || true
        wait "$bridgepid" >/dev/null 2>&1 || true
    fi
    if [[ "$owns_model" == 1 ]]; then
        python3 "$src/tools/rc-13-windows-ollama-bridge.py" unload ||
            echo 'RC-13 CLEANUP WARNING: could not confirm release of model'
    fi
    rm -rf -- "$work"
    if [[ "$result" == 0 ]]; then
        echo 'RC-13 CLEANUP PASS'
    else
        echo "RC-13 CLEANUP after exit $result"
    fi
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -m 700 "$src"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$src"
test -f "$src/tools/rc-13-windows-ollama-bridge.py" || {
    echo 'RC-13 NOT_RUN: bridge absent from exact SHA'
    exit 2
}
test -f "$src/tools/ResidentChat.ModelEvaluation/Program.cs" || {
    echo 'RC-13 NOT_RUN: C# evaluator absent from exact SHA'
    exit 2
}

echo 'RC-13 STEP 1: check existing Windows-local Ollama via Windows curl.exe'
state="$(python3 "$src/tools/rc-13-windows-ollama-bridge.py" state)" || {
    echo 'RC-13 NOT_RUN: Windows Ollama or WSL Windows interop unavailable'
    exit 2
}
[[ "$state" == "loaded" || "$state" == "notloaded" ]] || {
    echo 'RC-13 NOT_RUN: unexpected Ollama model state'
    exit 2
}
echo "RC-13 MODEL STATE: $state"

echo 'RC-13 STEP 2: compile real C# classifier evaluator offline'
timeout 180 docker run --rm --pull never --network none --read-only \
    --cpus 2 --memory 1g --memory-swap 1g --pids-limit 128 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$src,dst=/work" \
    --mount "type=bind,src=$cache,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$packages" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e DOTNET_NOLOGO=1 \
    "$sdk" /bin/bash -c '
    set -Eeuo pipefail
    dotnet restore tools/ResidentChat.ModelEvaluation/ResidentChat.ModelEvaluation.csproj \
        --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
    dotnet build tools/ResidentChat.ModelEvaluation/ResidentChat.ModelEvaluation.csproj \
        -c Release --no-restore -o /work/.rc13-eval -v quiet
    '
test -s "$src/.rc13-eval/ResidentChat.ModelEvaluation.dll" || {
    echo 'RC-13 FAIL: C# evaluator did not compile'
    exit 1
}

if [[ "$state" == "notloaded" ]]; then
    owns_model=1
    echo 'RC-13 STEP 3: load only existing Qwen model for synthetic run'
    python3 "$src/tools/rc-13-windows-ollama-bridge.py" warmup || {
        echo 'RC-13 NOT_RUN: local Qwen warmup failed'
        exit 2
    }
else
    echo 'RC-13 STEP 3: reuse preloaded Qwen'
fi

echo 'RC-13 STEP 4: temporary WSL localhost-only JSON bridge'
python3 "$src/tools/rc-13-windows-ollama-bridge.py" serve \
    >"$work/bridge.log" 2>&1 &
bridgepid=$!
ready=0
for ((attempt=0;attempt<40;attempt++)); do
    if curl --noproxy '*' -fsS -m 2 \
       'http://127.0.0.1:11434/_rc13_health' >/dev/null 2>&1; then
       ready=1
       break
    fi
    if ! kill -0 "$bridgepid" 2>/dev/null; then break; fi
    sleep 0.25
done
[[ "$ready" == 1 ]] || {
    echo 'RC-13 NOT_RUN: WSL local test bridge failed to bind/initialize'
    exit 2
}
echo 'RC-13 STEP 5: actual C# HttpClient JSON classifier -> local Qwen'
timeout 150 docker run --rm --pull never --network host --read-only \
    --cpus 1 --memory 512m --memory-swap 512m --pids-limit 96 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /app \
    --tmpfs /tmp:rw,nosuid,nodev,size=64m \
    --mount "type=bind,src=$src/.rc13-eval,dst=/app,readonly" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    "$sdk" dotnet /app/ResidentChat.ModelEvaluation.dll --synthetic-loaded-model
echo "RC-13 CSHARP + LIVE LOCAL QWEN PASS sha=$sha"
echo 'RC-13 LIMIT: six fictional examples; Telegram/database/browser not exercised'
