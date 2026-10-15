#!/usr/bin/env bash
# RC-07: disposable SYNTHETIC browser lab. NO real residents or Telegram.
# --run serves a loopback-only web UI; --self-test verifies and exits.
set -Eeuo pipefail
umask 077

if [[ $# != 2 || ! "$1" =~ ^[0-9a-f]{40}$ || "$2" != "--run" && "$2" != "--self-test" ]]; then
    echo 'RC-07 USAGE: bash rc-07-local-browser.sh <PINNED_SHA> --run|--self-test'
    exit 2
fi
sha="$1"
mode="$2"
if [[ "$(id -u)" == 0 ]]; then
    echo 'RC-07 REFUSED: do not execute as root'
    exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
cache=/home/user/ResidentChatPRAutotest/nuget
lock=/home/user/WSLAutotest/execution.lock
sdk=jobradar-pr-autotest:dotnet10
postgres=postgres:17-bookworm
port=18760
database=residentchat_approval_lab_browser
for command_name in git docker tar flock mktemp curl openssl timeout; do
    command -v "$command_name" >/dev/null || {
        echo "RC-07 NOT_RUN: missing command $command_name"
        exit 2
    }
done
[[ -f "$lock" && -d "$mirror" ]] || {
    echo 'RC-07 NOT_RUN: WSL2 lab mirror/lock missing'
    exit 2
}
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC-07 NOT_RUN: shared test lock busy'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo 'RC-07 NOT_RUN: unknown pinned commit'
    exit 2
}
if [[ -d "$cache/packages/npgsql" ]]; then
    packages=/nuget/packages
elif [[ -d "$cache/npgsql" ]]; then
    packages=/nuget
else
    echo 'RC-07 NOT_RUN: offline NuGet cache unavailable'
    exit 2
fi
docker image inspect "$sdk" >/dev/null || { echo 'RC-07 NOT_RUN: SDK image missing'; exit 2; }
docker image inspect "$postgres" >/dev/null || { echo 'RC-07 NOT_RUN: PostgreSQL image missing'; exit 2; }

# Never interfere with an existing service. No wildcard Docker cleanup.
if curl --noproxy '*' -s -m 2 -o /dev/null "http://127.0.0.1:$port/"; then
    echo "RC-07 REFUSED: port $port is already in use"
    exit 2
fi

work="$(mktemp -d /tmp/rc07-browser.XXXXXXXX)"
src="$work/source"
socket="$work/socket"
id="$(basename "$work" | tr -cd '[:alnum:]')"
pg="rc07-pg-$id"
api="rc07-api-$id"
pg_started=0
api_started=0
chmod 711 "$work"
cleanup() {
    code=$?
    trap - EXIT INT TERM
    if [[ "$api_started" == 1 ]]; then
        docker stop -t 5 "$api" >/dev/null 2>&1 || true
        docker rm -f "$api" >/dev/null 2>&1 || true
    fi
    if [[ "$pg_started" == 1 ]]; then
        docker stop -t 5 "$pg" >/dev/null 2>&1 || true
        docker rm -f "$pg" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work"
    if [[ "$code" == 0 || "$code" == 130 ]]; then
        echo 'RC-07 CLEANUP PASS: temporary DB and lab secrets removed'
    else
        echo "RC-07 FAILED (code $code): temporary DB and lab secrets removed" >&2
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
for required in src/ResidentChat.Api/ResidentChat.Api.csproj \
    tools/ResidentChat.LocalLabToken/Program.cs \
    db/migrations/001_ticket_workflow.sql; do
    [[ -f "$src/$required" ]] || { echo "RC-07 NOT_RUN: missing $required"; exit 2; }
done

# Random NEW signing/audit keys every invocation, never committed or reused.
signing="$(openssl rand -hex 32)"
audit="$(openssl rand -hex 32)"
[[ "$signing" != "$audit" ]] || exit 2

# A socket-only PostgreSQL; no published TCP port, no container network.
docker run -d --pull never --name "$pg" --network none \
    --cpus 1 --memory 512m --pids-limit 128 \
    --tmpfs /var/lib/postgresql/data:rw,nosuid,nodev,size=256m \
    --mount "type=bind,src=$socket,dst=/var/run/postgresql" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    "$postgres" -c listen_addresses= >/dev/null
pg_started=1

ready=0
for ((attempt=0;attempt<80;attempt++)); do
    if docker exec "$pg" pg_isready -h /var/run/postgresql \
        -U postgres -d postgres >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 0.5
done
[[ "$ready" == 1 ]] || { echo 'RC-07 FAIL: disposable Postgres not ready'; exit 1; }
docker exec "$pg" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d postgres \
    -c "CREATE DATABASE $database" >/dev/null
docker exec -i "$pg" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d "$database" \
    < "$src/db/migrations/001_ticket_workflow.sql" >/dev/null

# Runtime login is non-owner/non-superuser and inherits forced RLS.
docker exec -i "$pg" psql -X -q -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d "$database" >/dev/null <<'SQL'
CREATE ROLE residentchat_synthetic_app LOGIN;
GRANT USAGE ON SCHEMA residentchat TO residentchat_synthetic_app;
GRANT SELECT, INSERT, UPDATE ON residentchat.tickets TO residentchat_synthetic_app;
GRANT SELECT, INSERT ON residentchat.ticket_audit TO residentchat_synthetic_app;
GRANT USAGE, SELECT ON SEQUENCE residentchat.ticket_audit_id_seq TO residentchat_synthetic_app;
SQL

cat > "$work/runtime.env" <<EOF
ResidentChat__Operator__Enabled=true
ResidentChat__Operator__Issuer=residentchat.synthetic.test
ResidentChat__Operator__Audience=residentchat.synthetic.operator
ResidentChat__Operator__JwtSigningKey=$signing
ResidentChat__Operator__AuditHmacKey=$audit
ResidentChat__Operator__ConnectionString=Host=$socket;Database=$database;Username=residentchat_synthetic_app;Pooling=false;Timeout=5
ResidentChat__Operator__AllowedTenantIds__0=11111111-1111-4111-8111-111111111111
ResidentChat__Operator__AllowedTenantIds__1=22222222-2222-4222-8222-222222222222
ResidentChat__OperatorConsole__Enabled=true
ResidentChat__OperatorConsole__Mode=synthetic-lab-only
ResidentChat__Telegram__Enabled=false
ASPNETCORE_URLS=http://127.0.0.1:$port
ASPNETCORE_ENVIRONMENT=Production
DOTNET_CLI_HOME=/tmp/dotnet-home
DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
cat > "$work/token.env" <<EOF
RC07_SIGNING_KEY=$signing
DOTNET_CLI_HOME=/tmp/dotnet-home
DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
unset signing audit
chmod 600 "$work/runtime.env" "$work/token.env"

echo 'RC-07 building API and synthetic token tool entirely offline...'
timeout 300 docker run --rm --pull never --network none \
    --read-only --cpus 2 --memory 1g --memory-swap 1g --pids-limit 128 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$src,dst=/work" \
    --mount "type=bind,src=$cache,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$packages" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    "$sdk" /bin/bash -c '
    set -Eeuo pipefail
    dotnet restore src/ResidentChat.Api/ResidentChat.Api.csproj \
        --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
    dotnet publish src/ResidentChat.Api/ResidentChat.Api.csproj \
        -c Release --no-restore -o /work/.lab-api -v quiet
    dotnet restore tools/ResidentChat.LocalLabToken/ResidentChat.LocalLabToken.csproj \
        --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
    dotnet publish tools/ResidentChat.LocalLabToken/ResidentChat.LocalLabToken.csproj \
        -c Release --no-restore -o /work/.lab-token -v quiet
    '

# Docker host networking preserves the loopback socket source IP, which
# the RC-06 console validates. Kestrel binds ONLY 127.0.0.1:18760.
# In this synthetic-only lab NO egress firewall guarantee is implied.
docker run -d --pull never --name "$api" --network host \
    --read-only --cpus 2 --memory 768m --memory-swap 768m --pids-limit 128 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /app \
    --tmpfs /tmp:rw,nosuid,nodev,size=64m \
    --mount "type=bind,src=$src/.lab-api,dst=/app,readonly" \
    --mount "type=bind,src=$socket,dst=$socket" \
    --env-file "$work/runtime.env" \
    "$sdk" dotnet /app/ResidentChat.Api.dll >/dev/null
api_started=1

tokens() {
    docker run --rm --pull never --network none --read-only \
        --cpus 1 --memory 128m --pids-limit 64 \
        --cap-drop ALL --security-opt no-new-privileges:true \
        --user "$(id -u):$(id -g)" --workdir /lab \
        --tmpfs /tmp:rw,nosuid,nodev,size=32m \
        --mount "type=bind,src=$src/.lab-token,dst=/lab,readonly" \
        --env-file "$work/token.env" \
        "$sdk" dotnet /lab/ResidentChat.LocalLabToken.dll "$1"
}
alpha_token="$(tokens alpha)"
beta_token="$(tokens beta)"
[[ "$alpha_token" == eyJ* && "$beta_token" == eyJ* ]] || {
    echo 'RC-07 FAIL: synthetic token issuance failed'
    exit 1
}

ready=0
for ((attempt=0;attempt<40;attempt++)); do
    if curl --noproxy '*' -fsS -m 2 -o /dev/null \
        "http://127.0.0.1:$port/operator/console"; then
        ready=1
        break
    fi
    sleep 0.5
done
if [[ "$ready" != 1 ]]; then
    echo 'RC-07 FAIL: loopback console HTTP not ready' >&2
    docker logs --tail 15 "$api" >&2 || true
    exit 1
fi

url="http://127.0.0.1:$port"
if ! curl --noproxy '*' -fsS -m 5 \
    -H "Authorization: Bearer $alpha_token" \
    "$url/operator/console/v1/proposals" | grep -q 'ALPHA-WATER'; then
    echo 'RC-07 FAIL: alpha JWT / tenant queue validation' >&2
    exit 1
fi
if ! curl --noproxy '*' -fsS -m 5 \
    -H "Authorization: Bearer $beta_token" \
    "$url/operator/console/v1/proposals" | grep -q 'BETA-ELEVATOR'; then
    echo 'RC-07 FAIL: beta JWT / tenant queue validation' >&2
    exit 1
fi

# Release the global watcher lock once build is finished; session containers
# have unique names and are only removed by this script's own cleanup.
flock -u 9
echo
echo 'RC-07 LOCAL BROWSER GATE PASS'
echo 'The HTTP page and two independently signed, tenant-scoped tokens work.'
if [[ "$mode" == "--self-test" ]]; then
    echo 'RC-07 SELF-TEST PASS (no ticket writes; disposable DB)'
    exit 0
fi

echo
echo "OPEN IN WINDOWS: http://localhost:$port/operator/console"
echo
echo 'ALPHA test operator JWT (copy into the browser):'
printf '%s\n' "$alpha_token"
echo
echo 'BETA test operator JWT (other fictional building):'
printf '%s\n' "$beta_token"
echo
echo 'This lab is SYNTHETIC ONLY, never enter real credentials or messages.'
echo 'Keep this PowerShell/WSL window open while using the browser.'
echo 'Stop with Ctrl+C. Maximum session: 75 minutes; then cleanup.'
echo

# Keep the disposable lab alive. Verify the process every few seconds.
# Never use unbounded waiting, and never kill other containers.
deadline=$((SECONDS+4500))
while ((SECONDS < deadline)); do
    running="$(docker inspect --format '{{.State.Running}}' "$api" 2>/dev/null || true)"
    if [[ "$running" != true ]]; then
        echo 'RC-07 STOP: lab API exited' >&2
        docker logs --tail 15 "$api" >&2 || true
        exit 1
    fi
    sleep 5
done
echo 'RC-07 STOP: session expired'
