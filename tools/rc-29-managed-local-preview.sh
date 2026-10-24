#!/usr/bin/env bash
# RC-29 OWNER ONLY: prepare and start local read-only operator preview,
# inspect status, mint 15-min fictional operator JWT to Windows clipboard,
# or stop ONLY the managed RC-29 preview container.
# Never touch PostgreSQL container/volumes, real Telegram or external AI.
set -Eeuo pipefail
umask 077
if [[ $# != 2 || ! "$1" =~ ^[0-9a-f]{40}$ ||
      "$2" != "--prepare-start" && "$2" != "--status" &&
      "$2" != "--token-clipboard" && "$2" != "--stop" ||
      "$(id -u)" == 0 ]]; then
 echo 'RC29_USAGE=PINNED_SHA_AND_EXPLICIT_MODE_NONROOT'; exit 2
fi
sha="$1"
mode="$2"
owner_home="$HOME"
root="$owner_home/.local/share/residentchat"
secrets="$root/secrets"
ring="$root/data-protection/keys"
release="$root/releases/$sha"
name=residentchat-rc29-preview
pg=residentchat-pg17
data=residentchat-pg17-data
sock=residentchat-pg17-socket
sdk=jobradar-pr-autotest:dotnet10
lock=/home/user/WSLAutotest/execution.lock
mirror=/home/user/ResidentChatPRAutotest/source.git
nuget=/home/user/ResidentChatPRAutotest/nuget
port=18762
pgpw="$secrets/pg-runtime-password"
pfx="$secrets/rc28-dp-certificate.pfx"
pfxpw="$secrets/rc28-dp-password"
sign="$secrets/rc28-operator-jwt"
audit="$secrets/rc28-operator-audit"
for util in docker git flock grep stat python3; do
 command -v "$util" >/dev/null || { echo 'RC29_NOT_RUN=REQUIRED_UTILITY_MISSING'; exit 2; }
done
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC29_NOT_RUN=SHARED_WSL_LOCK_BUSY'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" 2>/dev/null || {
 echo 'RC29_NOT_RUN=PINNED_COMMIT_NOT_IN_MIRROR'; exit 2;
}
docker image inspect "$sdk" >/dev/null 2>&1 || {
 echo 'RC29_NOT_RUN=OFFLINE_DOTNET10_IMAGE_MISSING'; exit 2;
}

has_preview=0
docker container inspect "$name" >/dev/null 2>&1 && has_preview=1
if [[ "$has_preview" == 1 ]]; then
 [[ "$(docker inspect -f '{{index .Config.Labels "residentchat.mode"}}' "$name")" == owner-readonly-preview &&
    "$(docker inspect -f '{{index .Config.Labels "residentchat.commit"}}' "$name")" == "$sha" &&
    "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$name")" == host &&
    "$(docker inspect -f '{{.HostConfig.RestartPolicy.Name}}' "$name")" == no ]] || {
  echo 'RC29_STOP=EXISTING_PREVIEW_IDENTITY_MISMATCH'; exit 3
 }
fi

if [[ "$mode" == "--status" ]]; then
 if [[ "$has_preview" == 1 ]]; then
  echo "RC29_PREVIEW_RUNNING=$(docker inspect -f '{{.State.Running}}' "$name")"
 else
  echo 'RC29_PREVIEW_RUNNING=false'
 fi
 echo 'RC29_URL=http://127.0.0.1:18762/operator/console'
 echo 'RC29_TELEGRAM_POLLING=OFF'
 exit 0
fi

if [[ "$mode" == "--stop" ]]; then
 if [[ "$has_preview" == 1 ]]; then
  docker stop --time 12 "$name" >/dev/null 2>&1 || {
   echo 'RC29_STOP=PREVIEW_SHUTDOWN_FAILED'; exit 3;
  }
  # Remove ONLY labeled, explicitly named preview container; NO volumes.
  docker rm "$name" >/dev/null 2>&1 || {
   echo 'RC29_STOP=PREVIEW_REMOVE_FAILED'; exit 3;
  }
 fi
 echo 'RC29_LOCAL_PREVIEW_STOPPED=YES'
 echo 'RC29_PERMANENT_POSTGRES_UNTOUCHED=YES'
 echo 'RC29_LOCAL_KEYS_PRESERVED=YES'
 exit 0
fi

# Require exact existing owner local X509, signing and DB credentials.
for file in "$pgpw" "$pfx" "$pfxpw" "$sign" "$audit"; do
 [[ -f "$file" && ! -L "$file" && -s "$file" &&
    "$(stat -c %a "$file")" == 600 &&
    "$(stat -c %u "$file")" == "$(id -u)" ]] || {
  echo 'RC29_STOP=UNSAFE_OR_MISSING_OWNER_SECRET'; exit 3;
 }
done
[[ -d "$ring" && ! -L "$ring" &&
   "$(stat -c %a "$ring")" == 700 ]] || {
 echo 'RC29_STOP=ENCRYPTED_DP_KEYRING_MISSING'; exit 3;
}
[[ "$(docker inspect -f '{{.Config.Image}}' "$pg")" == postgres:17-bookworm &&
   "$(docker inspect -f '{{.State.Running}}' "$pg")" == true &&
   "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$pg")" == none ]] || {
 echo 'RC29_STOP=PERMANENT_POSTGRES_IDENTITY_INVALID'; exit 3;
}
docker volume inspect "$data" >/dev/null 2>&1 &&
docker volume inspect "$sock" >/dev/null 2>&1 || {
 echo 'RC29_STOP=PERMANENT_POSTGRES_VOLUME_MISSING'; exit 3;
}
pg_mounts="$(docker inspect -f '{{range .Mounts}}{{println .Name .Destination}}{{end}}' "$pg")"
grep -Fqx "$data /var/lib/postgresql/data" <<<"$pg_mounts" &&
grep -Fqx "$sock /var/run/postgresql" <<<"$pg_mounts" || {
 echo 'RC29_STOP=POSTGRES_VOLUME_MISMATCH'; exit 3;
}
socket_gid="$(docker exec -u 0 "$pg" stat -c %g /var/run/postgresql/.s.PGSQL.5432 2>/dev/null)" || {
 echo 'RC29_STOP=POSTGRES_UNIX_SOCKET_UNAVAILABLE'; exit 3;
}
[[ "$socket_gid" =~ ^[0-9]{1,7}$ ]] || {
 echo 'RC29_STOP=UNIX_SOCKET_GROUP_INVALID'; exit 3;
}

release_ready() {
 [[ -d "$release" && ! -L "$release" &&
    -s "$release/ResidentChat.OperatorHost.dll" &&
    -s "$release/operator-ui/index.html" &&
    -s "$release/.rc29-source-sha" ]] || return 1
 [[ "$(<"$release/.rc29-source-sha")" == "$sha" ]] || return 1
 [[ -s "$release/.rc29-hashes" ]] || return 1
 (cd "$release" && sha256sum -c --status .rc29-hashes) || return 1
}
if [[ "$mode" == "--token-clipboard" ]]; then
 [[ "$has_preview" == 1 &&
    "$(docker inspect -f '{{.State.Running}}' "$name")" == true ]] || {
  echo 'RC29_STOP=PREVIEW_NOT_RUNNING'; exit 3;
 }
 release_ready || { echo 'RC29_STOP=RELEASE_CHECKSUM_FAILED'; exit 3; }
 [[ -x /mnt/c/Windows/System32/clip.exe ]] || {
  echo 'RC29_NOT_RUN=WINDOWS_CLIPBOARD_INTEROP_UNAVAILABLE'; exit 2;
 }
 token_dir="$(mktemp -d /dev/shm/rc29-token.XXXXXXXX)"
 trap 'rm -rf -- "$token_dir"' EXIT
 chmod 700 "$token_dir"
 # Owner-only standard Python library, NO network, no process CLI secrets.
 # Token file is in /dev/shm, owner-only; never printed or persisted to disk.
 python3 - "$sign" "$token_dir/token.jwt" <<'PY'
import base64, hashlib, hmac, json, os, pathlib, secrets, sys, time
try:
    key = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").strip()
    if len(key) < 96 or len(key) > 512 or any(c not in "0123456789abcdefABCDEF" for c in key):
        raise ValueError("invalid key")
    def b64(x):
        return base64.urlsafe_b64encode(x).rstrip(b"=").decode("ascii")
    def enc(x):
        return b64(json.dumps(x, separators=(",", ":"), ensure_ascii=True).encode("ascii"))
    now = int(time.time())
    header = enc({"alg": "HS256", "typ": "JWT"})
    payload = enc({
        "iss": "residentchat.rc29.local-operator",
        "aud": "residentchat.rc29.owner-preview",
        "sub": "rc29-owner-lab-operator",
        "tenant_id": "27272727-2727-4727-8727-272727272727",
        "role": "operator",
        "jti": secrets.token_hex(16),
        "iat": now, "nbf": now, "exp": now + 900
    })
    signed = header + "." + payload
    signature = b64(hmac.new(key.encode("utf-8"), signed.encode("ascii"),
                             hashlib.sha256).digest())
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    fd = os.open(sys.argv[2], flags, 0o600)
    with os.fdopen(fd, "w", encoding="ascii") as out:
        out.write(signed + "." + signature)
except Exception:
    print("RC29_TOKEN_ISSUE_FAIL_SAFE", file=sys.stderr)
    raise SystemExit(1)
PY
 [[ -s "$token_dir/token.jwt" &&
    "$(stat -c %a "$token_dir/token.jwt")" == 600 ]] || {
  echo 'RC29_STOP=SHORT_LIVED_JWT_FILE_INVALID'; exit 3;
 }
 /mnt/c/Windows/System32/clip.exe < "$token_dir/token.jwt" || {
  echo 'RC29_STOP=WINDOWS_CLIPBOARD_WRITE_FAILED'; exit 3;
 }
 echo 'RC29_SHORT_LIVED_JWT_COPIED_TO_WINDOWS_CLIPBOARD=YES'
 echo 'RC29_JWT_VALIDITY_MINUTES=15'
 echo 'RC29_CLEAR_CLIPBOARD_AFTER_USE=RECOMMENDED'
 exit 0
fi

[[ "$mode" == "--prepare-start" ]] || { echo 'RC29_STOP=UNSUPPORTED_MODE'; exit 2; }
if [[ "$has_preview" == 1 ]]; then
 if [[ "$(docker inspect -f '{{.State.Running}}' "$name")" == true ]]; then
  release_ready || { echo 'RC29_STOP=RUNNING_RELEASE_FAILED_CHECKSUM'; exit 3; }
  echo 'RC29_ALREADY_RUNNING=YES'
  echo 'RC29_LOCAL_URL=http://127.0.0.1:18762/operator/console'
  exit 0
 fi
 echo 'RC29_STOP=EXISTING_STOPPED_PREVIEW_REQUIRES_EXPLICIT_STOP'; exit 3;
fi
command -v curl >/dev/null && command -v timeout >/dev/null &&
command -v sha256sum >/dev/null || {
 echo 'RC29_NOT_RUN=HEALTH_CHECK_DEPENDENCY_MISSING'; exit 2;
}
if curl --noproxy '*' -s -m 2 -o /dev/null "http://127.0.0.1:$port/health"; then
 echo 'RC29_STOP=LOOPBACK_PORT_OCCUPIED'; exit 3;
fi
if command -v ss >/dev/null && ss -ltn | grep -Eq "[:.]$port[[:space:]]"; then
 echo 'RC29_STOP=LOOPBACK_PORT_LISTENER_EXISTS'; exit 3
fi
if [[ -e "$release" ]]; then
 release_ready || { echo 'RC29_STOP=EXISTING_RELEASE_INTEGRITY_FAILURE'; exit 3; }
 echo 'RC29_VERIFIED_IMMUTABLE_RELEASE_REUSED=YES'
else
 for tool in node npm tar mktemp; do
  command -v "$tool" >/dev/null || { echo 'RC29_NOT_RUN=BUILD_DEPENDENCY_MISSING'; exit 2; }
 done
 node -e '
const [major,minor]=process.versions.node.split(".").map(Number);
if(!((major===22&&minor>=12)||major===24))process.exit(2);
' || { echo 'RC29_NOT_RUN=UNSUPPORTED_NODE'; exit 2; }
 if [[ -d "$nuget/packages/npgsql" ]]; then packages=/nuget/packages
 elif [[ -d "$nuget/npgsql" ]]; then packages=/nuget
 else echo 'RC29_NOT_RUN=OFFLINE_NUGET_MISSING'; exit 2; fi
 work="$(mktemp -d /tmp/rc29-build.XXXXXXXX)"
 trap 'rm -rf -- "$work"' EXIT
 mkdir -m 700 "$work/src" "$work/npm-cache" "$work/isolated-home"
 git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/src"
 [[ -s "$work/src/frontend/package-lock.json" ]] || {
  echo 'RC29_STOP=PINNED_NPM_LOCKFILE_MISSING'; exit 3;
 }
 : > "$work/user.npmrc"
 : > "$work/global.npmrc"
 chmod 600 "$work/user.npmrc" "$work/global.npmrc"
 export HOME="$work/isolated-home"
 export npm_config_cache="$work/npm-cache"
 export npm_config_userconfig="$work/user.npmrc"
 export npm_config_globalconfig="$work/global.npmrc"
 export npm_config_registry=https://registry.npmjs.org/
 export npm_config_audit=false
 export npm_config_fund=false
 export npm_config_ignore_scripts=true
 export npm_config_update_notifier=false
 export npm_config_progress=false
 export NODE_ENV=development
 export NODE_OPTIONS=--max-old-space-size=512
 unset NPM_TOKEN NODE_AUTH_TOKEN npm_config__authToken
 echo 'RC29_OWNER_REACT_BUILD_STARTED'
 (
  cd "$work/src/frontend"
  timeout 240 npm ci --ignore-scripts --no-audit --no-fund \
   --fetch-retries=1 --fetch-timeout=15000
  timeout 240 npm run check
  timeout 60 npm run verify:bundle
 )
 echo 'RC29_OWNER_REACT_BUILD_PASS'
 echo 'RC29_OFFLINE_OPERATOR_HOST_PUBLISH_STARTED'
 timeout 300 docker run --rm --pull never \
  --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
  --pids-limit 128 --cap-drop ALL --security-opt no-new-privileges:true \
  --user "$(id -u):$(id -g)" --workdir /work \
  --tmpfs /tmp:rw,nosuid,nodev,size=128m \
  --mount "type=bind,src=$work/src,dst=/work" \
  --mount "type=bind,src=$nuget,dst=/nuget,readonly" \
  -e "NUGET_PACKAGES=$packages" -e DOTNET_CLI_HOME=/tmp/dotnet-home \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -e DOTNET_NOLOGO=1 \
  "$sdk" /bin/bash -c '
   set -Eeuo pipefail
   dotnet restore tools/ResidentChat.OperatorHost/ResidentChat.OperatorHost.csproj \
    --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
   dotnet publish tools/ResidentChat.OperatorHost/ResidentChat.OperatorHost.csproj \
    -c Release --no-restore -o /work/.rc29-host -v quiet'
 mkdir -p "$work/src/.rc29-host/operator-ui"
 cp -a "$work/src/frontend/dist/." "$work/src/.rc29-host/operator-ui/"
 [[ -s "$work/src/.rc29-host/ResidentChat.OperatorHost.dll" &&
    -s "$work/src/.rc29-host/operator-ui/index.html" ]] || {
  echo 'RC29_STOP=DOTNET_REACT_OUTPUT_INCOMPLETE'; exit 3;
 }
 mkdir -p -m 700 "$root/releases"
 stage="$root/releases/.rc29-stage-$sha"
 [[ ! -e "$stage" && ! -L "$stage" ]] || {
  echo 'RC29_STOP=OLD_INCOMPLETE_RELEASE_STAGE_PRESENT'; exit 3;
 }
 mkdir -m 700 "$stage"
 cp -a "$work/src/.rc29-host/." "$stage/"
 printf '%s\n' "$sha" > "$stage/.rc29-source-sha"
 (
  cd "$stage"
  find . -type f ! -name .rc29-hashes -print0 |
   sort -z | xargs -0 sha256sum > .rc29-hashes
 )
 mv "$stage" "$release"
 echo 'RC29_IMMUTABLE_RELEASE_PREPARED=YES'
fi
release_ready || { echo 'RC29_STOP=RELEASE_HASHES_INVALID'; exit 3; }
# HOME may have been changed by the isolated npm build; never derive
# cryptographic file locations from it after source packaging.
echo 'RC29_OWNER_SECRETS_REUSED=YES'
docker run -d --pull never --name "$name" \
 --label residentchat.mode=owner-readonly-preview \
 --label "residentchat.commit=$sha" \
 --restart no --network host --read-only \
 --cpus 2 --memory 768m --memory-swap 768m --pids-limit 128 \
 --cap-drop ALL --security-opt no-new-privileges:true \
 --user "$(id -u):$(id -g)" --group-add "$socket_gid" \
 --workdir /app \
 --tmpfs /tmp:rw,nosuid,nodev,size=64m \
 --mount "type=bind,src=$release,dst=/app,readonly" \
 --mount "type=volume,src=$sock,dst=/var/run/postgresql" \
 --mount "type=bind,src=$ring,dst=/run/rc29-data-protection/keys" \
 --mount "type=bind,src=$pgpw,dst=/run/rc29-secrets/pg-runtime-password,readonly" \
 --mount "type=bind,src=$pfx,dst=/run/rc29-secrets/rc28-dp-certificate.pfx,readonly" \
 --mount "type=bind,src=$pfxpw,dst=/run/rc29-secrets/rc28-dp-password,readonly" \
 --mount "type=bind,src=$sign,dst=/run/rc29-secrets/rc28-operator-jwt,readonly" \
 --mount "type=bind,src=$audit,dst=/run/rc29-secrets/rc28-operator-audit,readonly" \
 -e DOTNET_CLI_HOME=/tmp/dotnet-home \
 -e ASPNETCORE_ENVIRONMENT=Production \
 -e ASPNETCORE_URLS=http://127.0.0.1:18762 \
 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
 --log-driver local --log-opt max-size=4m --log-opt max-file=2 \
 "$sdk" dotnet /app/ResidentChat.OperatorHost.dll --serve-owner-readonly >/dev/null
ready=0
for ((i=0;i<40;i++)); do
 if curl --noproxy '*' --fail -s --max-time 2 \
   "http://127.0.0.1:$port/health" >/dev/null 2>&1; then
  ready=1; break
 fi
 sleep 0.5
done
if [[ "$ready" != 1 ]]; then
 echo 'RC29_STOP=PREVIEW_NOT_READY_CONTAINER_PRESERVED_FOR_SAFE_INSPECTION'; exit 3
fi
status="$(curl --noproxy '*' -s --max-time 5 \
 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/operator/console")"
unauth="$(curl --noproxy '*' -s --max-time 5 \
 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/operator/console/v1/proposals")"
mutate="$(curl --noproxy '*' -s --max-time 5 -X POST \
 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/operator/v1/tickets")"
[[ "$status" == 200 && "$unauth" == 401 && "$mutate" == 405 ]] || {
 echo 'RC29_STOP=LOCAL_HTTP_POLICY_CHECK_FAILED_CONTAINER_PRESERVED'; exit 3
}
# Index and assets must come from React 19 Vite, not legacy fallback.
index_html="$(curl --noproxy '*' --fail -s --max-time 5 "http://127.0.0.1:$port/operator/console")" || {
 echo 'RC29_STOP=REACT_INDEX_UNAVAILABLE'; exit 3;
}
grep -q 'id="root"' <<< "$index_html" || {
 echo 'RC29_STOP=REACT_MOUNT_ROOT_MISSING'; exit 3;
}
assets="$(grep -oE '/operator/console/assets/index-[a-zA-Z0-9_-]+[.](js|css)' <<< "$index_html" | sort -u)" || {
 echo 'RC29_STOP=REACT_HASH_ASSETS_MISSING'; exit 3;
}
[[ "$assets" == *'.js'* && "$assets" == *'.css'* ]] || {
 echo 'RC29_STOP=REACT_JS_OR_CSS_MISSING'; exit 3;
}
while IFS= read -r asset; do
 [[ -n "$asset" ]] || continue
 asset_status="$(curl --noproxy '*' -s --max-time 5 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port$asset")"
 [[ "$asset_status" == 200 ]] || {
  echo 'RC29_STOP=REACT_ASSET_HTTP_FAILURE'; exit 3;
 }
done <<< "$assets"
echo 'RC29_REACT_STATIC_ASSETS_HTTP_PASS'
echo 'RC29_LOCAL_REACT_HTTP=200'
echo 'RC29_ANONYMOUS_QUEUE_HTTP=401'
echo 'RC29_MUTATING_POST_HTTP=405'
echo 'RC29_NETWORK_BIND=127.0.0.1:18762'
echo 'RC29_DATABASE=residentchat'
echo 'RC29_PERSISTENT_KEYS=REUSED'
echo 'RC29_TELEGRAM_POLLING=OFF'
echo 'RC29_TELEGRAM_UPDATES_ACKED=NO'
echo 'RC29_RESTART_POLICY=NO_AUTOSTART'
echo 'RC29_LOCAL_URL=http://127.0.0.1:18762/operator/console'
echo 'RC29_MANAGED_LOCAL_PREVIEW_STARTED_PASS'
