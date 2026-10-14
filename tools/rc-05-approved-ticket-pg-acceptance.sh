#!/usr/bin/env bash
# RC-05: a REAL PostgreSQL 17 / signed JWT / operator HTTP acceptance.
# All inputs, accounts, tickets and approvals are synthetic fixtures.
# Separate manual one-shot gate; no real Telegram, Qwen or external network.
set -Eeuo pipefail
umask 077

sha="$1"
# No flag => automated regression that SIMULATES a human confirmation.
# Explicit flags => one manually authorized synthetic approval and one POST.
if [[ "$#" == 1 ]]; then
    approval_case=""
    approval_confirmation=""
elif [[ "$#" == 5 && "$2" == "--approve" && "$3" == "ALPHA-WATER"
        && "$4" == "--confirm" && "$5" == "APPROVE ALPHA-WATER" ]]; then
    approval_case="$3"
    approval_confirmation="$5"
else
    echo "RC-05 NOT_RUN: use SHA [--approve ALPHA-WATER --confirm 'APPROVE ALPHA-WATER']"
    exit 2
fi
if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]] || [[ "$(id -u)" == 0 ]]; then
    echo "RC-05 NOT_RUN: use exact SHA as unprivileged WSL user"
    exit 2
fi
mirror="/home/user/ResidentChatPRAutotest/source.git"
cache="/home/user/ResidentChatPRAutotest/nuget"
lock="/home/user/WSLAutotest/execution.lock"
sdk_image="jobradar-pr-autotest:dotnet10"
pg_image="postgres:17-bookworm"
database="residentchat_approval_lab_synthetic"
for cmd in git docker tar flock timeout mktemp; do
    command -v "$cmd" >/dev/null || { echo "RC-05 NOT_RUN: host utility missing"; exit 2; }
done

exec 9<> "$lock"
flock -w 120 9 || { echo "RC-05 NOT_RUN: shared WSL lock occupied"; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo "RC-05 NOT_RUN: pinned commit absent from local mirror"
    exit 2
}
if [[ -d "$cache/packages/npgsql" ]]; then
    nuget_mount="/nuget/packages"
elif [[ -d "$cache/npgsql" ]]; then
    nuget_mount="/nuget"
else
    echo "RC-05 NOT_RUN: offline Npgsql package cache unavailable"
    exit 2
fi
docker image inspect "$sdk_image" >/dev/null || { echo "RC-05 NOT_RUN: offline SDK image absent"; exit 2; }
docker image inspect "$pg_image" >/dev/null || { echo "RC-05 NOT_RUN: offline PostgreSQL image absent"; exit 2; }

work="$(mktemp -d /tmp/rc05pg.XXXXXXXX)"
src="$work/src"
socket="$work/socket"
container="rc05-$(basename "$work" | tr -cd '[:alnum:]')"
pg_started=0

# The Postgres image runs as a separate Unix UID; grant traverse on parent.
chmod 711 "$work"
cleanup() {
    code=$?
    trap - EXIT
    if [[ "$pg_started" == 1 ]]; then
        if [[ "$code" != 0 ]]; then
            echo "RC-05 PostgreSQL container state (synthetic-only):" >&2
            docker inspect --format 'Status={{.State.Status}} ExitCode={{.State.ExitCode}} OOM={{.State.OOMKilled}}' "$container" >&2 || true
            docker logs --tail 20 "$container" >&2 || true
        fi
        docker stop --time 10 "$container" >/dev/null 2>&1 || true
        docker rm -f "$container" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work"
    if [[ "$code" == 0 ]]; then
        echo "RC-05 CLEANUP PASS"
    else
        echo "RC-05 CLEANUP after failing test"
    fi
    exit "$code"
}
trap cleanup EXIT

mkdir -m 700 "$src"
mkdir -m 777 "$socket"
chmod 777 "$socket"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$src"
if [[ ! -f "$src/tests/ResidentChat.OperatorSmokeTests/Program.cs" ]]; then
    echo "RC-05 NOT_RUN: operator acceptance source missing"
    exit 2
fi

echo "RC-05 pinned source: $sha"
echo "RC-05 starting disposable PostgreSQL 17..."
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
[[ "$ready" == 1 ]] || { echo "RC-05 FAIL: PostgreSQL startup failed"; exit 1; }

docker exec "$container" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d postgres \
    -c "CREATE DATABASE $database" >/dev/null

echo "RC-05 verifying real signed-JWT operator HTTP, approval gate and RLS..."
timeout 300 docker run --rm --pull never \
    --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
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
    -e "RC05_APPROVE_CASE=$approval_case" \
    -e "RC05_APPROVE_CONFIRMATION=$approval_confirmation" \
    -e "RC02_TEST_PG_ADMIN=Host=$socket;Database=$database;Username=postgres;Pooling=false;Timeout=5;Command Timeout=20" \
    -e "RC02_TEST_PG_RUNTIME=Host=$socket;Database=$database;Username=residentchat_synthetic_app;Pooling=true;Timeout=5;Command Timeout=20" \
    "$sdk_image" /bin/bash -c \
    'set -Eeuo pipefail
     dotnet restore tests/ResidentChat.OperatorSmokeTests/ResidentChat.OperatorSmokeTests.csproj --source "$NUGET_PACKAGES" --ignore-failed-sources --verbosity quiet
     if [[ -n "$RC05_APPROVE_CASE" ]]; then
         dotnet run -c Release --no-restore --no-launch-profile --project tests/ResidentChat.OperatorSmokeTests/ResidentChat.OperatorSmokeTests.csproj -- \
             --approve "$RC05_APPROVE_CASE" --confirm "$RC05_APPROVE_CONFIRMATION"
     else
         dotnet run -c Release --no-restore --no-launch-profile --project tests/ResidentChat.OperatorSmokeTests/ResidentChat.OperatorSmokeTests.csproj
     fi'

if [[ -n "$approval_case" ]]; then
    echo "RC-05 OPERATOR-TRIGGERED SYNTHETIC DATABASE DEMO PASS"
else
    echo "RC-05 REAL POSTGRESQL OPERATOR APPROVAL ACCEPTANCE PASS (scripted synthetic operator)"
fi
