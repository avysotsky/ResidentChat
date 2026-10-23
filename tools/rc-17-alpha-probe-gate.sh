#!/usr/bin/env bash
# RC-17: synthetic Telegram webhook -> local Qwen C# -> PostgreSQL 17 ->
# signed JWT operator HTTP consumed by React (NO real Telegram/PII).
set -Eeuo pipefail
umask 077
sha="${1:-}"
update_id="${2:-}"
if [[ $# != 2 || ! "$sha" =~ ^[0-9a-f]{40}$ ||
      ! "$update_id" =~ ^[1-9][0-9]{0,17}$ || "$(id -u)" == 0 ]]; then
    echo 'RC-17 NOT_RUN: exact SHA and ordinary WSL user required'
    exit 2
fi
mirror="/home/user/ResidentChatPRAutotest/source.git"
cache="/home/user/ResidentChatPRAutotest/nuget"
lock="/home/user/WSLAutotest/execution.lock"
sdk_image="jobradar-pr-autotest:dotnet10"
pg_image="postgres:17-bookworm"
database="residentchat_approval_lab_rc17"
for cmd in git docker tar flock timeout mktemp python3 curl; do
    command -v "$cmd" >/dev/null || { echo "RC-17 NOT_RUN: host utility missing"; exit 2; }
done

exec 9<> "$lock"
flock -w 120 9 || { echo "RC-17 NOT_RUN: shared WSL lock occupied"; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo "RC-17 NOT_RUN: pinned commit absent from local mirror"
    exit 2
}
if [[ -d "$cache/packages/npgsql" ]]; then
    nuget_mount="/nuget/packages"
elif [[ -d "$cache/npgsql" ]]; then
    nuget_mount="/nuget"
else
    echo "RC-17 NOT_RUN: offline Npgsql package cache unavailable"
    exit 2
fi
docker image inspect "$sdk_image" >/dev/null || { echo "RC-17 NOT_RUN: offline SDK image absent"; exit 2; }
docker image inspect "$pg_image" >/dev/null || { echo "RC-17 NOT_RUN: offline PostgreSQL image absent"; exit 2; }

work="$(mktemp -d /tmp/rc05pg.XXXXXXXX)"
src="$work/src"
socket="$work/socket"
container="rc17-$(basename "$work" | tr -cd '[:alnum:]')"
pg_started=0
bridgepid=""
owns_model=0
completed=0

# The Postgres image runs as a separate Unix UID; grant traverse on parent.
chmod 711 "$work"
cleanup() {
    code=$?
    trap - EXIT INT TERM
    if [[ "$code" == 0 && "$completed" != 1 ]]; then
        echo 'RC-17 FAIL: script ended before classified webhook assertions' >&2
        code=1
    fi
    if [[ -n "$bridgepid" ]]; then
        kill "$bridgepid" >/dev/null 2>&1 || true
        wait "$bridgepid" >/dev/null 2>&1 || true
    fi
    if [[ "$owns_model" == 1 ]]; then
        python3 "$src/tools/rc-13-windows-ollama-bridge.py" unload </dev/null ||
            echo 'RC-17 WARNING: Qwen unload could not be confirmed'
    fi
    if [[ "$pg_started" == 1 ]]; then
        if [[ "$code" != 0 ]]; then
            echo "RC-17 PostgreSQL container state (synthetic-only):" >&2
            docker inspect --format 'Status={{.State.Status}} ExitCode={{.State.ExitCode}} OOM={{.State.OOMKilled}}' "$container" >&2 || true
            docker logs --tail 20 "$container" >&2 || true
        fi
        docker stop --time 10 "$container" >/dev/null 2>&1 || true
        docker rm -f "$container" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work"
    if [[ "$code" == 0 ]]; then
        echo "RC-17 CLEANUP PASS"
    else
        echo "RC-17 CLEANUP after failing test"
    fi
    exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -m 700 "$src"
mkdir -m 777 "$socket"
chmod 777 "$socket"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$src"
if [[ ! -f "$src/tests/ResidentChat.OperatorSmokeTests/Program.cs" ]]; then
    echo "RC-17 NOT_RUN: operator acceptance source missing"
    exit 2
fi

echo "RC-17 pinned source: $sha"
echo "RC-17 STEP 1: disposable PostgreSQL 17 (Unix socket only)..."
# Official entrypoint clears PGHOST when bootstrapping; mount standard socket.
docker run -d --pull never --name "$container" \
    --network none --cpus 1 --memory 512m --pids-limit 128 \
    --tmpfs /var/lib/postgresql/data:rw,nosuid,nodev,size=256m \
    --mount "type=bind,src=$socket,dst=/var/run/postgresql" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    "$pg_image" -c listen_addresses= >/dev/null
pg_started=1

ready=0
for _ in $(seq 1 80); do
    if docker exec "$container" pg_isready \
        -h /var/run/postgresql -U postgres -d postgres >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 0.5
done
[[ "$ready" == 1 ]] || { echo "RC-17 FAIL: PostgreSQL startup failed"; exit 1; }

docker exec "$container" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d postgres \
    -c "CREATE DATABASE $database" >/dev/null

echo 'RC-17 STEP 2: Windows-local Qwen preflight, no pull/download'
bridge="$src/tools/rc-13-windows-ollama-bridge.py"
test -f "$bridge" || { echo 'RC-17 NOT_RUN: pinned bridge missing'; exit 2; }
state="$(python3 "$bridge" state </dev/null)" || {
    echo 'RC-17 NOT_RUN: Windows Ollama/interop unavailable'; exit 2
}
[[ "$state" == "loaded" || "$state" == "notloaded" ]] || {
    echo 'RC-17 NOT_RUN: invalid local model state'; exit 2
}
if [[ "$state" == "notloaded" ]]; then
    owns_model=1
    echo 'RC-17 warming already-installed local Qwen'
    python3 "$bridge" warmup </dev/null || {
        echo 'RC-17 NOT_RUN: synthetic model warmup failed'; exit 2
    }
else
    echo 'RC-17 reusing already-loaded Qwen'
fi
echo 'RC-17 STEP 3: temporary WSL loopback-only Ollama bridge'
python3 "$bridge" serve </dev/null >"$work/bridge.log" 2>&1 &
bridgepid=$!
ready_bridge=0
for ((attempt=0;attempt<40;attempt++)); do
    if kill -0 "$bridgepid" 2>/dev/null &&
       curl --noproxy '*' -fsS -m 2 \
         'http://127.0.0.1:11434/_rc13_health' >/dev/null 2>&1; then
        ready_bridge=1
        break
    fi
    if ! kill -0 "$bridgepid" 2>/dev/null; then break; fi
    sleep 0.25
done
[[ "$ready_bridge" == 1 ]] || {
    echo 'RC-17 NOT_RUN: ephemeral WSL model bridge failed'; exit 2
}
echo 'RC-17 STEP 4: real .NET10 webhook -> Qwen -> PostgreSQL -> operator HTTP'

timeout 300 docker run --rm --pull never \
    --network host --read-only --cpus 2 --memory 1g --memory-swap 1g \
    --pids-limit 128 --cap-drop ALL \
    --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$src,dst=/work" \
    --mount "type=bind,src=$socket,dst=$socket" \
    --mount "type=bind,src=$cache,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$nuget_mount" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e DOTNET_NOLOGO=1 \
    -e RC05_TEST_ISOLATED=1 \
    -e "RC17_PROBE_UPDATE_ID=$update_id" \
    -e "RC02_TEST_PG_ADMIN=Host=$socket;Database=$database;Username=postgres;Pooling=false;Timeout=5;Command Timeout=20" \
    -e "RC02_TEST_PG_RUNTIME=Host=$socket;Database=$database;Username=residentchat_synthetic_app;Pooling=true;Timeout=5;Command Timeout=20" \
    "$sdk_image" /bin/bash -c \
    'set -Eeuo pipefail
     dotnet restore tests/ResidentChat.OperatorSmokeTests/ResidentChat.OperatorSmokeTests.csproj --source "$NUGET_PACKAGES" --ignore-failed-sources --verbosity quiet
     dotnet run -c Release --no-restore --no-launch-profile \
         --project tests/ResidentChat.OperatorSmokeTests/ResidentChat.OperatorSmokeTests.csproj \
         -- --alpha-telegram-probe-live'
completed=1
echo "RC-17 REAL LAB-ALPHA PROBE (SANITIZED) + QWEN + POSTGRESQL PASS sha=$sha"
echo 'RC-17 LIMIT: real Telegram getUpdates was read on Windows; WSL receives only numeric update ID, reconstructs synthetic marker; polling offsets UNCHANGED, no actual TelegramLongPollingWorker run'
