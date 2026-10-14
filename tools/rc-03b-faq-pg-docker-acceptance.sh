#!/usr/bin/env bash
# Manual one-shot PostgreSQL acceptance for RC-03b, using existing local Docker images.
# This script does not change user systemd services, runner code, shared NuGet
# cache or any production database. It emits only synthetic test outcomes.
set -Eeuo pipefail
umask 077

sha="$1"
if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]]; then
    echo "RC-03B NOT_RUN: expected exact 40-character Git SHA"
    exit 2
fi
if [[ "$(id -u)" == "0" ]]; then
    echo "RC-03B NOT_RUN: run as unprivileged WSL user"
    exit 2
fi

mirror="/home/user/ResidentChatPRAutotest/source.git"
cache="/home/user/ResidentChatPRAutotest/nuget"
lock="/home/user/WSLAutotest/execution.lock"
sdk_image="jobradar-pr-autotest:dotnet10"
pg_image="postgres:17-bookworm"

for binary in git tar docker flock mktemp; do
    command -v "$binary" >/dev/null || {
        echo "RC-03B NOT_RUN: missing host utility $binary"
        exit 2
    }
done

# The existing shared lock is never deleted or replaced.
exec 9<> "$lock"
if ! flock -w 120 9; then
    echo "RC-03B NOT_RUN: shared runner lock busy"
    exit 2
fi

if ! git --git-dir="$mirror" cat-file -e "$sha"; then
    echo "RC-03B NOT_RUN: exact SHA not present in local runner Git cache"
    exit 2
fi
# The established WSL2 runner's NuGet bind may store expanded package
# directories directly under nuget/, not under an extra nuget/packages/.
# Detect both layouts; keep the shared cache strictly read-only.
if [[ -d "$cache/packages/npgsql" ]]; then
    nuget_mount="/nuget/packages"
elif [[ -d "$cache/npgsql" ]]; then
    nuget_mount="/nuget"
else
    echo "RC-03B NOT_RUN: Npgsql package not present in shared offline NuGet cache"
    exit 2
fi
docker image inspect "$sdk_image" >/dev/null || {
    echo "RC-03B NOT_RUN: local .NET 10 image missing"
    exit 2
}
docker image inspect "$pg_image" >/dev/null || {
    echo "RC-03B NOT_RUN: local PostgreSQL 17 image missing"
    exit 2
}

work="$(mktemp -d /tmp/rc03bpg.XXXXXXXX)"
source_dir="$work/src"
socket_dir="$work/socket"
pg_container="rc03b-$(basename "$work" | tr -cd '[:alnum:]')"
database="residentchat_faq_acceptance_ci"
pg_started=0
# PostgreSQL runs under the image's postgres UID, not the invoking WSL UID.
# Allow directory traversal for that UID while keeping source code 0700.
# The socket directory is world-writable only inside a short-lived,
# random-named host directory, with Docker TCP networking disabled.
chmod 711 "$work"

cleanup() {
    code=$?
    trap - EXIT
    if [[ "$pg_started" == "1" ]]; then
        if [[ "$code" != "0" ]]; then
            # The short-lived disposable database has synthetic content only.
            # Docker state and bounded startup logs establish the REAL cause
            # before the container is removed; do not log secrets or SQL rows.
            echo "=== PostgreSQL container state ===" >&2
            docker inspect --format 'Status={{.State.Status}} ExitCode={{.State.ExitCode}} OOM={{.State.OOMKilled}}' "$pg_container" >&2 || true
            echo "=== PostgreSQL startup log (last 30 lines) ===" >&2
            docker logs --tail 30 "$pg_container" >&2 || true
        fi
        # Graceful shutdown removes the Unix socket and permits normal
        # cleanup by the unprivileged WSL owner of the parent directory.
        docker stop --time 10 "$pg_container" >/dev/null 2>&1 || true
        docker rm -f "$pg_container" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work"
    if [[ "$code" == "0" ]]; then
        echo "RC-03B CLEANUP PASS"
    else
        echo "RC-03B CLEANUP performed after failure"
    fi
    exit "$code"
}
trap cleanup EXIT

mkdir -m 700 "$source_dir"
mkdir -m 777 "$socket_dir"
chmod 777 "$socket_dir"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$source_dir"
if [[ ! -f "$source_dir/tests/ResidentChat.FaqPgAcceptance/Program.cs" ]]; then
    echo "RC-03B NOT_RUN: acceptance project absent at requested SHA"
    exit 2
fi

echo "RC-03B SOURCE pinned SHA: $sha"
echo "RC-03B starting disposable offline PostgreSQL..."
# The official image bootstraps via psql after deliberately clearing PGHOST.
# Bind our ephemeral socket directory at /var/run/postgresql, the default
# bootstrap path. The .NET client sees the same socket under $socket_dir.
docker run -d --pull never --name "$pg_container" \
    --network none --cpus 1 --memory 512m --pids-limit 128 \
    --tmpfs /var/lib/postgresql/data:rw,nosuid,nodev,size=256m \
    --mount "type=bind,src=$socket_dir,dst=/var/run/postgresql" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    "$pg_image" -c listen_addresses= >/dev/null
pg_started=1

ready=0
for i in $(seq 1 80); do
    if docker exec "$pg_container" \
        pg_isready -h /var/run/postgresql -U postgres -d postgres >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 0.5
done
if [[ "$ready" != "1" ]]; then
    echo "RC-03B FAIL: temporary PostgreSQL did not start"
    exit 1
fi

docker exec "$pg_container" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d postgres \
    -c "CREATE DATABASE $database" >/dev/null
docker exec "$pg_container" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d "$database" \
    -c "CREATE ROLE residentchat_faq_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS" >/dev/null

echo "RC-03B running .NET 10 acceptance with NO Docker network..."
timeout 300 docker run --rm --pull never \
    --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
    --pids-limit 128 --cap-drop ALL \
    --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$source_dir,dst=/work" \
    --mount "type=bind,src=$socket_dir,dst=$socket_dir" \
    --mount "type=bind,src=$cache,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$nuget_mount" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e DOTNET_NOLOGO=1 \
    -e "RC03B_TEST_PG_ADMIN=Host=$socket_dir;Database=$database;Username=postgres;Pooling=false;Timeout=5;Command Timeout=20" \
    -e "RC03B_TEST_PG_RUNTIME=Host=$socket_dir;Database=$database;Username=residentchat_faq_reader;Pooling=true;Timeout=5;Command Timeout=20" \
    "$sdk_image" /bin/bash -c \
    'set -Eeuo pipefail
     dotnet restore tests/ResidentChat.FaqPgAcceptance/ResidentChat.FaqPgAcceptance.csproj --source "$NUGET_PACKAGES" --ignore-failed-sources --verbosity quiet
     dotnet run --project tests/ResidentChat.FaqPgAcceptance/ResidentChat.FaqPgAcceptance.csproj -c Release --no-restore --no-launch-profile'

echo "RC-03B POSTGRESQL ACCEPTANCE PASS (manual, not yet a watcher report)"
