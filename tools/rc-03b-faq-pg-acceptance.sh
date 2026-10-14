#!/usr/bin/env bash
# RC-03b: disposable, Unix-socket-only PostgreSQL acceptance harness.
# Run as an unprivileged Linux user under the existing WSL2 execution.lock.
# The invoking watcher owns SHA pinning, resource limits and immutable results.
set -Eeuo pipefail
umask 077

repo_root="$(cd -- "$(dirname -- "$0")/.." && pwd -P)"
project="$repo_root/tests/ResidentChat.FaqPgAcceptance/ResidentChat.FaqPgAcceptance.csproj"

if [[ "$(id -u)" == "0" ]]; then
    echo "RC-03B PG ACCEPTANCE NOT_RUN: unprivileged user required" >&2
    exit 2
fi
if ! command -v dotnet >/dev/null 2>&1; then
    echo "RC-03B PG ACCEPTANCE NOT_RUN: .NET SDK unavailable" >&2
    exit 2
fi

pgbin=""
if command -v initdb >/dev/null 2>&1 && command -v pg_ctl >/dev/null 2>&1; then
    pgbin="$(dirname -- "$(command -v initdb)")"
else
    for candidate in /usr/lib/postgresql/*/bin; do
        if [[ -x "$candidate/initdb" && -x "$candidate/pg_ctl" && -x "$candidate/psql" ]]; then
            pgbin="$candidate"
        fi
    done
fi
if [[ -z "$pgbin" || ! -x "$pgbin/psql" ]]; then
    echo "RC-03B PG ACCEPTANCE NOT_RUN: PostgreSQL binaries unavailable" >&2
    exit 2
fi

# A short private path avoids PostgreSQL Unix socket path length limits.
work="$(mktemp -d /tmp/rc03bpg.XXXXXXXX)"
data="$work/data"
socket="$work/socket"
mkdir -m 700 "$socket"
db="residentchat_faq_acceptance_$$"
pg_started=0

cleanup() {
    code=$?
    trap - EXIT
    if [[ "$pg_started" == "1" ]]; then
        "$pgbin/pg_ctl" -D "$data" -m immediate -w stop >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work"
    unset RC03B_TEST_PG_ADMIN RC03B_TEST_PG_RUNTIME
    if [[ "$code" == "0" ]]; then
        echo "RC-03B PG ACCEPTANCE CLEANUP PASS"
    else
        echo "RC-03B PG ACCEPTANCE CLEANUP FAIL"
    fi
    exit "$code"
}
trap cleanup EXIT

# Unix socket is under a mode-0700 directory and there is NO TCP listener.
# local trust auth is confined to this disposable private Unix socket.
"$pgbin/initdb" -D "$data" --no-instructions --auth-local=trust --auth-host=reject \
    >/dev/null 2>&1
"$pgbin/pg_ctl" -D "$data" -l "$work/server.log" \
    -o "-k $socket -c listen_addresses='' -c fsync=off" -w start >/dev/null 2>&1
pg_started=1

"$pgbin/psql" -h "$socket" -d postgres -v ON_ERROR_STOP=1 -q \
    -c "CREATE DATABASE $db" >/dev/null
"$pgbin/psql" -h "$socket" -d "$db" -v ON_ERROR_STOP=1 -q \
    -c "CREATE ROLE residentchat_faq_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS" >/dev/null

export RC03B_TEST_PG_ADMIN="Host=$socket;Database=$db;Username=$(id -un);Pooling=false;Timeout=5;Command Timeout=20"
export RC03B_TEST_PG_RUNTIME="Host=$socket;Database=$db;Username=residentchat_faq_reader;Pooling=true;Timeout=5;Command Timeout=20"

# --no-restore prevents the acceptance step from invoking an online NuGet feed.
# The supervising WSL2 runner performs its normal isolated restore first.
dotnet run --project "$project" -c Release --no-restore --no-launch-profile
