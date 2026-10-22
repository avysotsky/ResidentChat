#!/usr/bin/env bash
# RC-07: disposable SYNTHETIC browser lab. NO real residents or Telegram.
# --run is interactive; --self-test is read-only; --pg-acceptance runs genuine
# Kestrel and PostgreSQL 17 with fictional tenant JWTs and ticket writes.
set -Eeuo pipefail
umask 077

if [[ $# != 2 || ! "$1" =~ ^[0-9a-f]{40}$ || ( "$2" != "--run" && "$2" != "--self-test" && "$2" != "--pg-acceptance" ) ]]; then
    echo 'RC-07 USAGE: bash rc-07-local-browser.sh <PINNED_SHA> --run|--self-test|--pg-acceptance'
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
alpha_queue="$(curl --noproxy '*' -fsS -m 5 \
    -H "Authorization: Bearer $alpha_token" \
    "$url/operator/console/v1/proposals")" || {
    echo 'RC-07 FAIL: alpha JWT rejected' >&2
    exit 1
}
beta_queue="$(curl --noproxy '*' -fsS -m 5 \
    -H "Authorization: Bearer $beta_token" \
    "$url/operator/console/v1/proposals")" || {
    echo 'RC-07 FAIL: beta JWT rejected' >&2
    exit 1
}
[[ "$alpha_queue" == *ALPHA-WATER* && "$alpha_queue" != *BETA-ELEVATOR* \
    && "$beta_queue" == *BETA-ELEVATOR* && "$beta_queue" != *ALPHA-WATER* ]] || {
    echo 'RC-07 FAIL: cross-tenant proposal visibility' >&2
    exit 1
}
unset alpha_queue beta_queue

# Browser must have a bearer JWT; anonymous and mismatched tenants fail.
anonymous_code="$(curl --noproxy '*' -sS -m 5 -o /dev/null \
    -w '%{http_code}' "$url/operator/console/v1/proposals")"
[[ "$anonymous_code" == 401 ]] || {
    echo 'RC-07 FAIL: anonymous proposal read was not blocked' >&2
    exit 1
}
foreign_code="$(curl --noproxy '*' -sS -m 5 -o /dev/null \
    -w '%{http_code}' -H "Authorization: Bearer $beta_token" \
    -H 'Content-Type: application/json' \
    --data '{"confirmation":"APPROVE ALPHA-WATER"}' \
    "$url/operator/console/v1/proposals/ALPHA-WATER/approve")"
[[ "$foreign_code" == 404 ]] || {
    echo 'RC-07 FAIL: foreign tenant could access ALPHA approval' >&2
    exit 1
}
ticket_rows="$(docker exec "$pg" psql -X -tA -v ON_ERROR_STOP=1 \
    -h /var/run/postgresql -U postgres -d "$database" \
    -c 'SELECT count(*) FROM residentchat.tickets')"
[[ "$ticket_rows" == 0 ]] || {
    echo 'RC-07 FAIL: a ticket was persisted without authorization' >&2
    exit 1
}
echo 'RC-07 AUTHORIZATION GATE PASS: JWT required, cross-tenant denied, zero tickets'

if [[ "$mode" == "--pg-acceptance" ]]; then
    # These are the real Kestrel HTTP endpoints and genuine PostgreSQL 17
    # transactions, not a mocked store. Only synthetic Alpha/Beta data.
    # Never automatically retry a state-changing HTTP request.
    echo "RC-09 full-stack pinned SHA: $sha"
    code() {
        curl --noproxy '*' --silent --show-error --max-time 10 \
            -o /dev/null -w '%{http_code}' "$@"
    }
    expect_http() {
        if [[ "$1" != "$2" ]]; then
            echo "RC-09 FAIL: $3: expected HTTP $1, got $2" >&2
            exit 1
        fi
    }
    scalar() {
        docker exec "$pg" psql -X -tA -v ON_ERROR_STOP=1 \
            -h /var/run/postgresql -U postgres -d "$database" -c "$1"
    }

    expect_http 400 "$(code -H "Authorization: Bearer $alpha_token" \
        -H 'Content-Type: application/json' \
        --data '{"confirmation":"APPROVE BETA-ELEVATOR"}' \
        "$url/operator/console/v1/proposals/ALPHA-WATER/approve")" \
        'wrong case confirmation refused'
    [[ "$(scalar 'SELECT count(*) FROM residentchat.tickets')" == 0 ]] || {
        echo 'RC-09 FAIL: invalid approval persisted a ticket' >&2
        exit 1
    }
    expect_http 200 "$(code -H "Authorization: Bearer $alpha_token" \
        -H 'Content-Type: application/json' \
        --data '{"confirmation":"APPROVE ALPHA-WATER"}' \
        "$url/operator/console/v1/proposals/ALPHA-WATER/approve")" \
        'positive Alpha approval'
    persisted="$(scalar "SELECT tenant_id::text || '|' || ticket_id::text ||
        '|' || status::text || '|' || version::text FROM residentchat.tickets")"
    [[ "$persisted" =~ ^11111111-1111-4111-8111-111111111111\|[0-9a-f-]{36}\|2\|1$ ]] || {
        echo 'RC-09 FAIL: PostgreSQL tenant/Escalated/version 1 mismatch' >&2
        exit 1
    }
    ticket_id="$(printf '%s' "$persisted" | cut -d '|' -f2)"
    [[ "$(scalar 'SELECT count(*) FROM residentchat.ticket_audit')" == 1 ]] || {
        echo 'RC-09 FAIL: missing pseudonymous audit entry' >&2
        exit 1
    }
    alpha_ticket="$(curl --noproxy '*' --silent --show-error --fail \
        --max-time 10 -H "Authorization: Bearer $alpha_token" \
        "$url/operator/v1/tickets/$ticket_id")"
    [[ "$alpha_ticket" == *"\"ticketId\":\"$ticket_id\""* \
        && "$alpha_ticket" == *'"status":2'* \
        && "$alpha_ticket" == *'"version":1'* ]] || {
        echo 'RC-09 FAIL: Alpha signed status/version readback wrong' >&2
        exit 1
    }
    unset alpha_ticket
    expect_http 404 "$(code -H "Authorization: Bearer $beta_token" \
        "$url/operator/v1/tickets/$ticket_id")" \
        'Beta cross-tenant ticket read blocked'
    expect_http 409 "$(code -H "Authorization: Bearer $alpha_token" \
        -H 'Content-Type: application/json' \
        --data '{"confirmation":"APPROVE ALPHA-WATER"}' \
        "$url/operator/console/v1/proposals/ALPHA-WATER/approve")" \
        'duplicate Alpha approval rejected'
    expect_http 200 "$(code -H "Authorization: Bearer $beta_token" \
        -H 'Content-Type: application/json' \
        --data '{"confirmation":"REJECT BETA-ELEVATOR"}' \
        "$url/operator/console/v1/proposals/BETA-ELEVATOR/reject")" \
        'Beta rejection without write'
    expect_http 409 "$(code -H "Authorization: Bearer $beta_token" \
        -H 'Content-Type: application/json' \
        --data '{"confirmation":"APPROVE BETA-ELEVATOR"}' \
        "$url/operator/console/v1/proposals/BETA-ELEVATOR/approve")" \
        'rejected proposal cannot be approved'
    beta_queue="$(curl --noproxy '*' --silent --show-error --fail \
        --max-time 10 -H "Authorization: Bearer $beta_token" \
        "$url/operator/console/v1/proposals")"
    [[ "$beta_queue" == *'"caseId":"BETA-ELEVATOR"'* \
        && "$beta_queue" == *'"state":"Rejected"'* \
        && "$beta_queue" != *'"ticketId":"'* ]] || {
        echo 'RC-09 FAIL: Beta queue did not reflect rejection' >&2
        exit 1
    }
    unset beta_queue
    [[ "$(scalar 'SELECT count(*) FROM residentchat.tickets')" == 1 \
        && "$(scalar 'SELECT count(*) FROM residentchat.ticket_audit')" == 1 ]] || {
        echo 'RC-09 FAIL: rejection/duplicate changed SQL rows' >&2
        exit 1
    }
    echo 'RC-09 REAL POSTGRESQL ROWS / AUDIT / RLS / HTTP PASS'

    # Built React is a separate gate; vanilla fallback is NOT React PASS.
    if [[ -s "$src/.lab-api/operator-ui/index.html" ]]; then
        html="$(curl --noproxy '*' --silent --show-error --fail \
            --max-time 10 "$url/operator/console")"
        [[ "$html" == *'id="root"'* ]] || {
            echo 'RC-09 FAIL: React root absent from served index' >&2
            exit 1
        }
        assets="$(printf '%s' "$html" |
            grep -oE '/operator/console/assets/index-[A-Za-z0-9_-]+\.(js|css)' |
            sort -u)" || {
            echo 'RC-09 FAIL: Vite asset references missing' >&2
            exit 1
        }
        [[ "$assets" == *'.js'* && "$assets" == *'.css'* ]] || {
            echo 'RC-09 FAIL: React JS or CSS asset missing' >&2
            exit 1
        }
        while IFS= read -r asset; do
            [[ -n "$asset" ]] || continue
            expect_http 200 "$(code "$url$asset")" 'same-origin Vite asset'
        done <<< "$assets"
        echo 'RC-09 REACT BUNDLE SAME-ORIGIN HTTP PASS'
    else
        echo 'RC-09 REACT BUNDLE NOT_RUN: no validated Vite dist at pinned SHA'
    fi
    unset alpha_token beta_token ticket_id persisted
    echo "RC-09 REAL KESTREL + POSTGRESQL 17 ACCEPTANCE PASS sha=$sha"
    echo 'RC-09 only fictional tenants; disposable PostgreSQL is removed by cleanup'
    exit 0
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
