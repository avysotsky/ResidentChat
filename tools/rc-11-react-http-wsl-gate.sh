#!/usr/bin/env bash
# RC-11: exact-SHA React -> .NET 10 Kestrel same-origin HTTP acceptance.
# Fictional local configuration only. NO PostgreSQL, Telegram, Qwen, merge or deployment.
set -Eeuo pipefail
umask 077
sha="${1:-}"
if [[ $# != 1 || ! "$sha" =~ ^[a-f0-9]{40}$ || "$(id -u)" == 0 ]]; then
    echo 'RC-11 NOT_RUN: exact 40-character SHA, non-root WSL user required'
    exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
nuget=/home/user/ResidentChatPRAutotest/nuget
lock=/home/user/WSLAutotest/execution.lock
sdk=jobradar-pr-autotest:dotnet10
port=18761
for command_name in git node npm docker tar flock mktemp timeout curl openssl grep sort; do
    command -v "$command_name" >/dev/null || {
        echo "RC-11 NOT_RUN: missing command $command_name"
        exit 2
    }
done
[[ -d "$mirror" && -f "$lock" ]] || {
    echo 'RC-11 NOT_RUN: WSL watcher mirror or lock absent'
    exit 2
}
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo 'RC-11 NOT_RUN: watcher has not fetched pinned SHA'
    exit 2
}
if [[ -d "$nuget/packages/npgsql" ]]; then
    packages=/nuget/packages
elif [[ -d "$nuget/npgsql" ]]; then
    packages=/nuget
else
    echo 'RC-11 NOT_RUN: offline NuGet cache missing'
    exit 2
fi
docker image inspect "$sdk" >/dev/null 2>&1 || {
    echo 'RC-11 NOT_RUN: offline .NET 10 SDK image missing'
    exit 2
}
node -e '
const [major,minor] = process.versions.node.split(".").map(Number);
if (!((major===22 && minor>=12) || major===24)) process.exit(2);
' || { echo 'RC-11 NOT_RUN: requires Node 22.12+ or Node 24'; exit 2; }

echo "RC-11 NODE $(node --version), NPM $(npm --version)"
echo 'RC-11 waiting for existing shared WSL test lock (max 120 seconds)'
exec 9<>"$lock"
flock -w 120 9 || {
    echo 'RC-11 NOT_RUN: shared WSL test lock occupied'
    exit 2
}
if curl --noproxy '*' --silent -o /dev/null -m 2 "http://127.0.0.1:$port/"; then
    echo "RC-11 NOT_RUN: HTTP port $port already occupied"
    exit 2
fi

work="$(mktemp -d /tmp/rc11-react-http.XXXXXXXX)"
src="$work/source"
id="$(basename "$work" | tr -cd '[:alnum:]')"
api="rc11-api-$id"
api_started=0
cleanup() {
    code=$?
    trap - EXIT INT TERM
    if [[ "$api_started" == 1 ]]; then
        docker stop --time 5 "$api" >/dev/null 2>&1 || true
        docker rm -f "$api" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work"
    if [[ "$code" == 0 ]]; then
        echo 'RC-11 CLEANUP PASS'
    else
        echo "RC-11 CLEANUP after failure ($code)" >&2
    fi
    exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -m 700 "$src" "$work/npm-cache" "$work/home"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$src"
test -s "$src/frontend/package-lock.json" || {
    echo 'RC-11 NOT_RUN: pinned frontend/package-lock.json missing'
    exit 2
}

# Distinct empty npmrc files: npm 11 refuses double-loading a single file.
# No user npmrc, tokens, scripts, audits, external AI or resident-data traffic.
: > "$work/user.npmrc"
: > "$work/global.npmrc"
chmod 600 "$work/user.npmrc" "$work/global.npmrc"
export HOME="$work/home"
export npm_config_cache="$work/npm-cache"
export npm_config_userconfig="$work/user.npmrc"
export npm_config_globalconfig="$work/global.npmrc"
export npm_config_registry=https://registry.npmjs.org/
export npm_config_ignore_scripts=true
export npm_config_audit=false
export npm_config_fund=false
export npm_config_update_notifier=false
export npm_config_progress=false
export NODE_OPTIONS=--max-old-space-size=512
export NODE_ENV=development
unset NPM_TOKEN NODE_AUTH_TOKEN npm_config__authToken

echo 'RC-11 STEP 1: exact-lock npm ci and Vite build (temporary WSL copy)'
(
    cd "$src/frontend"
    timeout 240 npm ci --ignore-scripts --no-audit --no-fund \
        --fetch-retries=1 --fetch-timeout=15000
    timeout 240 npm run build
    timeout 60 npm run verify:bundle
)
test -s "$src/frontend/dist/index.html" || {
    echo 'RC-11 FAIL: Vite index missing after build'
    exit 1
}

echo 'RC-11 STEP 2: offline .NET 10 publish WITH bundled Vite assets'
timeout 300 docker run --rm --pull never --network none --read-only \
    --cpus 2 --memory 1g --memory-swap 1g --pids-limit 128 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$src,dst=/work" \
    --mount "type=bind,src=$nuget,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$packages" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    "$sdk" /bin/bash -c '
        set -Eeuo pipefail
        dotnet restore src/ResidentChat.Api/ResidentChat.Api.csproj \
            --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
        dotnet publish src/ResidentChat.Api/ResidentChat.Api.csproj \
            -c Release --no-restore -o /work/.rc11-api -v quiet
    '
test -s "$src/.rc11-api/operator-ui/index.html" || {
    echo 'RC-11 FAIL: React index was NOT packaged into .NET'
    exit 1
}

# Enable ONLY the synthetic loopback UI. Empty fictional DB hostname is NOT
# contacted; all operator API accesses in this gate are deliberately unauthenticated.
signing="$(openssl rand -hex 32)"
audit="$(openssl rand -hex 32)"
cat > "$work/runtime.env" <<EOF
ResidentChat__Operator__Enabled=true
ResidentChat__Operator__Issuer=residentchat.synthetic.rc11
ResidentChat__Operator__Audience=residentchat.synthetic.operator
ResidentChat__Operator__JwtSigningKey=$signing
ResidentChat__Operator__AuditHmacKey=$audit
ResidentChat__Operator__ConnectionString=Host=/nonexistent/rc11;Database=synthetic;Username=synthetic;Timeout=1
ResidentChat__Operator__AllowedTenantIds__0=11111111-1111-4111-8111-111111111111
ResidentChat__OperatorConsole__Enabled=true
ResidentChat__OperatorConsole__Mode=synthetic-lab-only
ResidentChat__Telegram__Enabled=false
ASPNETCORE_URLS=http://127.0.0.1:$port
ASPNETCORE_ENVIRONMENT=Production
DOTNET_CLI_HOME=/tmp/dotnet-home
DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
unset signing audit
chmod 600 "$work/runtime.env"

echo 'RC-11 STEP 3: start isolated synthetic Kestrel on 127.0.0.1'
docker run -d --pull never --name "$api" --network host \
    --read-only --cpus 2 --memory 768m --memory-swap 768m \
    --pids-limit 128 --cap-drop ALL \
    --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /app \
    --tmpfs /tmp:rw,nosuid,nodev,size=64m \
    --mount "type=bind,src=$src/.rc11-api,dst=/app,readonly" \
    --env-file "$work/runtime.env" \
    "$sdk" dotnet /app/ResidentChat.Api.dll >/dev/null
api_started=1

url="http://127.0.0.1:$port"
ready=0
for ((i=0;i<40;i++)); do
    if curl --noproxy '*' --fail --silent --max-time 2 \
        "$url/health" > "$work/health.json"; then
        ready=1
        break
    fi
    sleep 0.5
done
if [[ "$ready" != 1 ]]; then
    echo 'RC-11 FAIL: Kestrel not ready' >&2
    docker logs --tail 20 "$api" >&2 || true
    exit 1
fi

echo 'RC-11 STEP 4: verify React HTML and JS/CSS over SAME-origin HTTP'
index_status="$(curl --noproxy '*' --silent --show-error \
    --max-time 10 -D "$work/index.headers" -o "$work/index.html" \
    -w '%{http_code}' "$url/operator/console")"
[[ "$index_status" == 200 ]] || {
    echo "RC-11 FAIL: index HTTP $index_status" >&2
    exit 1
}
grep -q 'id="root"' "$work/index.html" || {
    echo 'RC-11 FAIL: React mount root absent (fallback page?)' >&2
    exit 1
}
grep -qi '^content-security-policy:' "$work/index.headers" || {
    echo 'RC-11 FAIL: missing strict CSP' >&2
    exit 1
}
assets="$(grep -oE '/operator/console/assets/index-[a-zA-Z0-9_-]+[.](js|css)' \
    "$work/index.html" | sort -u)" || {
    echo 'RC-11 FAIL: no Vite hash-named assets in index' >&2
    exit 1
}
[[ "$assets" == *'.js'* && "$assets" == *'.css'* ]] || {
    echo 'RC-11 FAIL: missing Vite JS or CSS reference' >&2
    exit 1
}
count=0
while IFS= read -r asset; do
    [[ -n "$asset" ]] || continue
    status="$(curl --noproxy '*' --silent --show-error \
        --max-time 10 -o "$work/asset-body" -w '%{http_code}' "$url$asset")"
    [[ "$status" == 200 && -s "$work/asset-body" ]] || {
        echo "RC-11 FAIL: invalid served Vite asset ($status)" >&2
        exit 1
    }
    count=$((count+1))
done <<< "$assets"
[[ "$count" == 2 ]] || {
    echo "RC-11 FAIL: unexpected Vite asset count $count" >&2
    exit 1
}
anonymous="$(curl --noproxy '*' --silent --show-error --max-time 10 \
    -o /dev/null -w '%{http_code}' "$url/operator/console/v1/proposals")"
[[ "$anonymous" == 401 ]] || {
    echo "RC-11 FAIL: operator API did not reject anonymous read ($anonymous)" >&2
    exit 1
}
invalid="$(curl --noproxy '*' --silent --show-error --max-time 10 \
    -o /dev/null -w '%{http_code}' "$url/operator/console/assets/forged.js")"
[[ "$invalid" == 404 ]] || {
    echo "RC-11 FAIL: arbitrary asset accepted ($invalid)" >&2
    exit 1
}
echo "RC-11 REACT + .NET 10 SAME-ORIGIN HTTP PASS sha=$sha"
echo 'RC-11 LIMIT: no browser automation, database, live Telegram or Qwen tested'
