#!/usr/bin/env bash
# RC-25: one-shot REAL ASP.NET10 API with EXISTING PERMANENT ResidentChat DB.
# Creates or reuses ONE metadata-only synthetic ticket and its audit in a
# RESERVED test tenant, retains it across two successive API instances.
# Never creates/restarts/removes DB container, named volumes or schema.
# No Telegram, Ollama, internet access or externally reachable API listener.
set -Eeuo pipefail
umask 077
if [[ $# != 1 || ! "$1" =~ ^[0-9a-f]{40}$ || "$(id -u)" == 0 ]]; then
 echo 'RC25_NOT_RUN=PINNED_COMMIT_OR_NONROOT_REQUIRED'; exit 2
fi
sha="$1"
mirror=/home/user/ResidentChatPRAutotest/source.git
lock=/home/user/WSLAutotest/execution.lock
pg_name=residentchat-pg17
pg_image=postgres:17-bookworm
data=residentchat-pg17-data
socket=residentchat-pg17-socket
sdk_image=jobradar-pr-autotest:dotnet10
pass_file="$HOME/.local/share/residentchat/secrets/pg-runtime-password"
cache=/home/user/ResidentChatPRAutotest/nuget
for utility in git docker tar flock mktemp grep; do
 command -v "$utility" >/dev/null || {
  echo 'RC25_NOT_RUN=MISSING_LOCAL_DEPENDENCY'; exit 2
 }
done
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC25_NOT_RUN=SHARED_LOCK_BUSY'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" 2>/dev/null || {
 echo 'RC25_NOT_RUN=SOURCE_COMMIT_MISSING'; exit 2;
}
docker image inspect "$sdk_image" >/dev/null 2>&1 || {
 echo 'RC25_NOT_RUN=OFFLINE_DOTNET10_IMAGE_MISSING'; exit 2;
}
docker container inspect "$pg_name" >/dev/null 2>&1 || {
 echo 'RC25_NOT_RUN=RESIDENTCHAT_PERMANENT_DB_MISSING'; exit 2;
}
docker volume inspect "$data" >/dev/null 2>&1 &&
docker volume inspect "$socket" >/dev/null 2>&1 || {
 echo 'RC25_NOT_RUN=RESIDENTCHAT_PERSISTENT_VOLUME_MISSING'; exit 2;
}
[[ "$(docker inspect -f '{{.State.Running}}' "$pg_name")" == true &&
   "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$pg_name")" == none &&
   "$(docker inspect -f '{{.Config.Image}}' "$pg_name")" == "$pg_image" ]] || {
 echo 'RC25_STOP=DB_CONTAINER_IDENTITY_OR_STATE_INVALID'; exit 3;
}
mounts="$(docker inspect -f '{{range .Mounts}}{{println .Name .Destination}}{{end}}' "$pg_name")"
grep -Fqx "$data /var/lib/postgresql/data" <<<"$mounts" &&
grep -Fqx "$socket /var/run/postgresql" <<<"$mounts" || {
 echo 'RC25_STOP=DB_VOLUME_MOUNT_MISMATCH'; exit 3;
}
[[ -f "$pass_file" && ! -L "$pass_file" && -s "$pass_file" &&
   "$(stat -c %a "$pass_file")" == 600 &&
   "$(stat -c %u "$pass_file")" == "$(id -u)" ]] || {
 echo 'RC25_STOP=LOCAL_RUNTIME_CREDENTIAL_UNAVAILABLE'; exit 3;
}
# The UNIX socket is restricted to the postgres group. Add its numeric
# GID to the one-shot SDK user; no chmod, port opening or server mutation.
socket_gid="$(docker exec -u 0 "$pg_name" stat -c %g /var/run/postgresql/.s.PGSQL.5432 2>/dev/null)" || {
 echo 'RC25_STOP=UNIX_SOCKET_MISSING'; exit 3;
}
[[ "$socket_gid" =~ ^[0-9]{1,7}$ ]] || {
 echo 'RC25_STOP=UNIX_SOCKET_GROUP_INVALID'; exit 3;
}
[[ -d "$cache/packages/npgsql" ]] && nuget_mount=/nuget/packages ||
 nuget_mount=/nuget
[[ -d "$cache/npgsql" || -d "$cache/packages/npgsql" ]] || {
 echo 'RC25_NOT_RUN=OFFLINE_NPGSQL_CACHE_MISSING'; exit 2;
}
work="$(mktemp -d /tmp/rc25-verify.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -m 700 "$work/src"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/src"
[[ -f "$work/src/tools/ResidentChat.PersistentApiAcceptance/Program.cs" ]] || {
 echo 'RC25_NOT_RUN=DOTNET10_PROBE_SOURCE_MISSING'; exit 2;
}
echo 'RC25_PERMANENT_DATABASE_ALREADY_EXISTS=YES'
echo 'RC25_DB_CONTAINER_RESTARTED=NO'
echo 'RC25_REAL_DOTNET10_API_BUILD_OFFLINE=START'
docker run --rm --pull never \
 --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
 --pids-limit 128 --cap-drop ALL \
 --security-opt no-new-privileges:true \
 --user "$(id -u):$(id -g)" --group-add "$socket_gid" \
 --workdir /work \
 --tmpfs /tmp:rw,nosuid,nodev,size=128m \
 --mount "type=bind,src=$work/src,dst=/work" \
 --mount "type=volume,src=$socket,dst=/var/run/postgresql" \
 --mount "type=bind,src=$pass_file,dst=/run/secrets/pg_runtime,readonly" \
 --mount "type=bind,src=$cache,dst=/nuget,readonly" \
 -e "NUGET_PACKAGES=$nuget_mount" \
 -e DOTNET_CLI_HOME=/tmp/dotnet-home \
 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
 -e DOTNET_NOLOGO=1 \
 "$sdk_image" /bin/bash -c \
 'set -Eeuo pipefail
  dotnet restore tools/ResidentChat.PersistentApiAcceptance/ResidentChat.PersistentApiAcceptance.csproj --source "$NUGET_PACKAGES" --ignore-failed-sources --verbosity quiet
  dotnet run --no-restore --no-launch-profile -c Release --project tools/ResidentChat.PersistentApiAcceptance/ResidentChat.PersistentApiAcceptance.csproj -- --persistent-api-once'
echo 'RC25_CREDENTIAL_SOURCE=LOCAL_READONLY_FILE'
echo 'RC25_DB_WRITES=MAX_ONE_SYNTHETIC_TICKET_IN_RESERVED_TENANT'
echo 'RC25_PERMANENT_API_SMOKE_FINISHED_PASS'
