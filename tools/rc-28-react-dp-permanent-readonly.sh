#!/usr/bin/env bash
# RC-28: owner-operated React + .NET10 + persisted X509 DataProtection.
# No Telegram/AI, no DB writes, no public listener, no installed service.
set -Eeuo pipefail
umask 077
if [[ $# != 1 || ! "$1" =~ ^[a-f0-9]{40}$ || "$(id -u)" == 0 ]]; then
 echo 'RC28_NOT_RUN=PINNED_SHA_NONROOT_REQUIRED'; exit 2
fi
sha="$1"
owner_home="$HOME"
mirror=/home/user/ResidentChatPRAutotest/source.git
lock=/home/user/WSLAutotest/execution.lock
nuget=/home/user/ResidentChatPRAutotest/nuget
sdk=jobradar-pr-autotest:dotnet10
pg=residentchat-pg17
sock=residentchat-pg17-socket
secrets="$owner_home/.local/share/residentchat/secrets"
ring="$owner_home/.local/share/residentchat/data-protection/keys"
pgpass="$secrets/pg-runtime-password"
pfx="$secrets/rc28-dp-certificate.pfx"
pfxpass="$secrets/rc28-dp-password"
signing="$secrets/rc28-operator-jwt"
audit="$secrets/rc28-operator-audit"
for utility in git docker node npm openssl flock mktemp tar timeout grep stat; do
 command -v "$utility" >/dev/null || { echo 'RC28_NOT_RUN=DEPENDENCY_MISSING'; exit 2; }
done
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC28_NOT_RUN=SHARED_LOCK_BUSY'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" 2>/dev/null || {
 echo 'RC28_NOT_RUN=PINNED_COMMIT_NOT_FETCHED'; exit 2;
}
docker image inspect "$sdk" >/dev/null 2>&1 || {
 echo 'RC28_NOT_RUN=OFFLINE_DOTNET10_IMAGE_MISSING'; exit 2;
}
[[ "$(docker inspect -f '{{.Config.Image}}' "$pg")" == postgres:17-bookworm &&
   "$(docker inspect -f '{{.State.Running}}' "$pg")" == true &&
   "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$pg")" == none ]] || {
 echo 'RC28_STOP=PERMANENT_PG_IDENTITY_INVALID'; exit 3;
}
docker volume inspect "$sock" >/dev/null 2>&1 || {
 echo 'RC28_STOP=PERSISTENT_PG_SOCKET_VOLUME_MISSING'; exit 3;
}
mounts="$(docker inspect -f '{{range .Mounts}}{{println .Name .Destination}}{{end}}' "$pg")"
grep -Fqx "$sock /var/run/postgresql" <<<"$mounts" || {
 echo 'RC28_STOP=PERMANENT_PG_SOCKET_MISMATCH'; exit 3;
}
[[ -f "$pgpass" && ! -L "$pgpass" && -s "$pgpass" &&
   "$(stat -c %a "$pgpass")" == 600 ]] || {
 echo 'RC28_STOP=LOCAL_RUNTIME_PG_SECRET_UNSAFE'; exit 3;
}
socket_gid="$(docker exec -u 0 "$pg" stat -c %g /var/run/postgresql/.s.PGSQL.5432 2>/dev/null)" || {
 echo 'RC28_STOP=PG_UNIX_SOCKET_MISSING'; exit 3;
}
[[ "$socket_gid" =~ ^[0-9]{1,7}$ ]] || {
 echo 'RC28_STOP=INVALID_SOCKET_GID'; exit 3;
}
if [[ -d "$nuget/packages/npgsql" ]]; then packages=/nuget/packages
elif [[ -d "$nuget/npgsql" ]]; then packages=/nuget
else echo 'RC28_NOT_RUN=OFFLINE_NUGET_CACHE_MISSING'; exit 2
fi
node -e '
const [major,minor]=process.versions.node.split(".").map(Number);
if(!((major===22&&minor>=12)||major===24))process.exit(2);
' || { echo 'RC28_NOT_RUN=UNSUPPORTED_NODE'; exit 2; }
work="$(mktemp -d /tmp/rc28-owner.XXXXXXXX)"
pem=""
cleanup() {
 code=$?
 trap - EXIT INT TERM
 if [[ -n "$pem" && -d "$pem" ]]; then rm -rf -- "$pem"; fi
 rm -rf -- "$work"
 if [[ "$code" == 0 ]]; then echo 'RC28_TEMP_BUILD_CLEANUP_PASS'; fi
 exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -m 700 "$work/src" "$work/home" "$work/npm-cache"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/src"
[[ -s "$work/src/frontend/package-lock.json" ]] || {
 echo 'RC28_STOP=PINNED_NPM_LOCKFILE_MISSING'; exit 3;
}

# Build public React dependencies BEFORE mounting/reading any RC28 secrets.
# No user npmrc, install scripts, audit/telemetry or private tokens.
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
echo 'RC28_REACT_NPM_CHECK_STARTED'
(
 cd "$work/src/frontend"
 timeout 240 npm ci --ignore-scripts --no-audit --no-fund \
   --fetch-retries=1 --fetch-timeout=15000
 timeout 240 npm run check
 timeout 60 npm run verify:bundle
)
[[ -s "$work/src/frontend/dist/index.html" ]] || {
 echo 'RC28_STOP=REACT_BUNDLE_MISSING'; exit 3;
}
echo 'RC28_REACT_NPM_CHECK_PASS'

echo 'RC28_DOTNET_PUBLISH_OFFLINE_STARTED'
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
  dotnet restore tools/ResidentChat.PersistentQueueAcceptance/ResidentChat.PersistentQueueAcceptance.csproj \
   --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
  dotnet publish tools/ResidentChat.PersistentQueueAcceptance/ResidentChat.PersistentQueueAcceptance.csproj \
   -c Release --no-restore -o /work/.rc28-acceptance -v quiet'
mkdir -p "$work/src/.rc28-acceptance/operator-ui"
cp -a "$work/src/frontend/dist/." "$work/src/.rc28-acceptance/operator-ui/"
[[ -s "$work/src/.rc28-acceptance/operator-ui/index.html" &&
   -s "$work/src/.rc28-acceptance/ResidentChat.PersistentQueueAcceptance.dll" ]] || {
 echo 'RC28_STOP=UNPACKED_REACT_DOTNET_BUNDLE_MISSING'; exit 3;
}
echo 'RC28_DOTNET_REACT_BUNDLE_READY=YES'

# Owner WSL home (saved across WSL restarts); never checked into Git.
real_secrets="$owner_home/.local/share/residentchat/secrets"
real_ring="$owner_home/.local/share/residentchat/data-protection/keys"
mkdir -p "$real_secrets" "$owner_home/.local/share/residentchat/data-protection"
chmod 700 "$real_secrets" "$owner_home/.local/share/residentchat/data-protection"
secrets="$real_secrets"
ring="$real_ring"
pfx="$secrets/rc28-dp-certificate.pfx"
pfxpass="$secrets/rc28-dp-password"
signing="$secrets/rc28-operator-jwt"
audit="$secrets/rc28-operator-audit"
present=0
for item in "$pfx" "$pfxpass" "$signing" "$audit"; do
 if [[ -e "$item" || -L "$item" ]]; then present=$((present+1)); fi
done
[[ "$present" == 0 || "$present" == 4 ]] || {
 echo 'RC28_STOP=PARTIAL_KEY_PROVISION_PRESERVED'; exit 3;
}
if [[ "$present" == 0 ]]; then
 [[ ! -e "$ring" && ! -L "$ring" ]] || {
  echo 'RC28_STOP=ORPHAN_KEYRING_PRESERVED'; exit 3;
 }
 [[ -d /dev/shm ]] || {
  echo 'RC28_STOP=NO_RAM_ONLY_CERT_GENERATION'; exit 3;
 }
 openssl rand -hex 48 > "$pfxpass"
 openssl rand -hex 48 > "$signing"
 openssl rand -hex 48 > "$audit"
 chmod 600 "$pfxpass" "$signing" "$audit"
 pem="$(mktemp -d /dev/shm/rc28-cert.XXXXXXXX)"
 chmod 700 "$pem"
 openssl req -x509 -newkey rsa:3072 -sha256 -days 730 -nodes \
  -subj '/CN=ResidentChat OwnerLocal LAB DP' \
  -keyout "$pem/private.pem" -out "$pem/public.pem" \
  >/dev/null 2>&1 || { echo 'RC28_STOP=LOCAL_CERT_CREATION_FAILED'; exit 3; }
 openssl pkcs12 -export -inkey "$pem/private.pem" -in "$pem/public.pem" \
  -out "$pem/encrypted.pfx" -passout "file:$pfxpass" \
  >/dev/null 2>&1 || { echo 'RC28_STOP=LOCAL_PFX_ENCRYPTION_FAILED'; exit 3; }
 chmod 600 "$pem/encrypted.pfx"
 mv "$pem/encrypted.pfx" "$pfx"
 mkdir -m 700 "$ring"
 echo 'RC28_OWNER_KEYS_CREATED=YES'
else
 [[ -d "$ring" && ! -L "$ring" ]] || {
  echo 'RC28_STOP=ORPHAN_CERT_WITHOUT_KEYRING'; exit 3;
 }
 for item in "$pfx" "$pfxpass" "$signing" "$audit"; do
  [[ -s "$item" && -f "$item" && ! -L "$item" &&
     "$(stat -c %a "$item")" == 600 &&
     "$(stat -c %u "$item")" == "$(id -u)" ]] || {
   echo 'RC28_STOP=SECRET_PERMISSIONS_INVALID'; exit 3;
  }
 done
 echo 'RC28_EXISTING_OWNER_KEYS_REUSED=YES'
fi
chmod 700 "$ring"

echo 'RC28_API_DP_REACT_ACCEPTANCE_STARTED'
timeout 180 docker run --rm --pull never \
 --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
 --pids-limit 128 --cap-drop ALL --security-opt no-new-privileges:true \
 --user "$(id -u):$(id -g)" --group-add "$socket_gid" \
 --workdir /work/.rc28-acceptance \
 --tmpfs /tmp:rw,nosuid,nodev,size=128m \
 --mount "type=bind,src=$work/src,dst=/work" \
 --mount "type=volume,src=$sock,dst=/var/run/postgresql" \
 --mount "type=bind,src=$pgpass,dst=/run/secrets/pg_runtime,readonly" \
 --mount "type=bind,src=$pfx,dst=/run/secrets/rc28_dp_cert,readonly" \
 --mount "type=bind,src=$pfxpass,dst=/run/secrets/rc28_dp_password,readonly" \
 --mount "type=bind,src=$signing,dst=/run/secrets/rc28_jwt,readonly" \
 --mount "type=bind,src=$audit,dst=/run/secrets/rc28_audit,readonly" \
 --mount "type=bind,src=$ring,dst=/run/rc28-data-protection/keys" \
 -e DOTNET_CLI_HOME=/tmp/dotnet-home -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
 -e DOTNET_NOLOGO=1 \
 "$sdk" dotnet /work/.rc28-acceptance/ResidentChat.PersistentQueueAcceptance.dll --react-dp-readonly
echo 'RC28_DATABASE=residentchat'
echo 'RC28_DATABASE_WRITES=NONE'
echo 'RC28_TELEGRAM_POLLING=OFF'
echo 'RC28_TELEGRAM_UPDATES_ACKED=NO'
echo 'RC28_NO_LONG_LIVED_SERVICE_INSTALLED=YES'
echo 'RC28_REACT_AND_ENCRYPTED_LOCAL_KEYS_PASS'
