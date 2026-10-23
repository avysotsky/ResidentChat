#!/usr/bin/env bash
# RC-22. Owner-operated, idempotent permanent PostgreSQL17 for ResidentChat.
# NEVER deletes volumes, database, schema, rows or local credentials.
# NO Telegram requests, local AI, bot polling or API deployment.
set -Eeuo pipefail
umask 077
[[ $# == 1 && "$1" =~ ^[0-9a-f]{40}$ && "$(id -u)" != 0 ]] || {
 echo 'RC22_NOT_RUN=REQUIRES_PINNED_SHA_AND_NONROOT'; exit 2;
}
sha="$1"
for tool in git docker flock tar mktemp openssl grep; do
 command -v "$tool" >/dev/null || { echo 'RC22_NOT_RUN=UTILITY_MISSING'; exit 2; }
done
repo=/home/user/ResidentChatPRAutotest/source.git
lock=/home/user/WSLAutotest/execution.lock
image=postgres:17-bookworm
name=residentchat-pg17
data=residentchat-pg17-data
sock=residentchat-pg17-socket
secrets="$HOME/.local/share/residentchat/secrets"
admin="$secrets/pg-admin-password"
runtime="$secrets/pg-runtime-password"
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC22_NOT_RUN=AUTOTEST_SHARED_LOCK_BUSY'; exit 2; }
git --git-dir="$repo" cat-file -e "$sha^{commit}" 2>/dev/null || {
 echo 'RC22_NOT_RUN=PINNED_COMMIT_NOT_FETCHED'; exit 2;
}
docker image inspect "$image" >/dev/null 2>&1 || {
 echo 'RC22_NOT_RUN=OFFLINE_POSTGRES_IMAGE_MISSING'; exit 2;
}
mkdir -p "$secrets"
chmod 0700 "$HOME/.local/share/residentchat" "$secrets"
container_found=0
data_found=0
sock_found=0
docker container inspect "$name" >/dev/null 2>&1 && container_found=1
docker volume inspect "$data" >/dev/null 2>&1 && data_found=1
docker volume inspect "$sock" >/dev/null 2>&1 && sock_found=1
if [[ "$container_found" == 0 && ( "$data_found" == 1 || "$sock_found" == 1 ) ]]; then
 echo 'RC22_STOP=ORPHAN_PERSISTENT_VOLUMES_REQUIRE_MANUAL_RECOVERY'; exit 3;
fi
if [[ "$container_found" == 1 && ( "$data_found" == 0 || "$sock_found" == 0 ) ]]; then
 echo 'RC22_STOP=EXISTING_CONTAINER_MISSING_EXPECTED_VOLUME'; exit 3;
fi

created=0
if [[ "$container_found" == 0 ]]; then
 [[ ! -e "$admin" && ! -e "$runtime" ]] || {
  echo 'RC22_STOP=EXISTING_SECRETS_WITHOUT_CONTAINER'; exit 3;
 }
 openssl rand -hex 32 > "$admin"
 openssl rand -hex 32 > "$runtime"
 # Bound directly into container; host parent 0700 blocks non-owner access.
 # Official PG entrypoint must read ADMIN file after switching to PG uid.
 chmod 0644 "$admin"
 chmod 0600 "$runtime"
 docker volume create "$data" >/dev/null
 docker volume create "$sock" >/dev/null
 docker run -d --pull never --name "$name" --restart unless-stopped \
   --network none --cpus 1 --memory 768m --memory-swap 768m \
   --pids-limit 128 --security-opt no-new-privileges:true \
   --mount "type=volume,src=$data,dst=/var/lib/postgresql/data" \
   --mount "type=volume,src=$sock,dst=/var/run/postgresql" \
   --mount "type=bind,src=$admin,dst=/run/secrets/pg_admin,readonly" \
   -e POSTGRES_PASSWORD_FILE=/run/secrets/pg_admin \
   -e POSTGRES_DB=residentchat \
   -e 'POSTGRES_INITDB_ARGS=--auth-local=scram-sha-256 --auth-host=scram-sha-256' \
   "$image" -c listen_addresses= -c unix_socket_permissions=0770 >/dev/null
 created=1
 echo 'RC22_NEW_PERMANENT_CLUSTER_CREATED=YES'
else
 [[ -s "$admin" && -s "$runtime" ]] || {
  echo 'RC22_STOP=LOCAL_CREDENTIALS_MISSING'; exit 3;
 }
 [[ "$(docker inspect -f '{{.Config.Image}}' "$name")" == "$image" &&
    "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$name")" == none ]] || {
  echo 'RC22_STOP=EXISTING_CONTAINER_IS_NOT_ISOLATED_POSTGRES17'; exit 3;
 }
 mounts="$(docker inspect -f '{{range .Mounts}}{{println .Name .Destination}}{{end}}' "$name")"
 grep -Fqx "$data /var/lib/postgresql/data" <<< "$mounts" &&
 grep -Fqx "$sock /var/run/postgresql" <<< "$mounts" || {
  echo 'RC22_STOP=PERSISTENT_VOLUME_MISMATCH'; exit 3;
 }
 ports="$(docker inspect -f '{{json .HostConfig.PortBindings}}' "$name")"
 [[ "$ports" == "{}" || "$ports" == null ]] || {
  echo 'RC22_STOP=POSTGRES_HAS_EXPOSED_PORTS'; exit 3;
 }
 if [[ "$(docker inspect -f '{{.State.Running}}' "$name")" != true ]]; then
  docker start "$name" >/dev/null
 fi
 echo 'RC22_EXISTING_CLUSTER_REUSED=YES'
fi

# pg admin credential is read from a file inside the already isolated
# Docker container. It is never part of CLI arguments or Docker metadata.
pg() {
 docker exec -i --user 0 "$name" sh -c \
  'PGPASSWORD="$(cat /run/secrets/pg_admin)"; export PGPASSWORD; exec psql -X -q -v ON_ERROR_STOP=1 -U postgres -d residentchat'
}
ready=0
for ((i=0;i<90;i++)); do
 if docker exec "$name" pg_isready -q -h /var/run/postgresql -U postgres -d residentchat; then
  ready=1
  break
 fi
 sleep 1
done
[[ "$ready" == 1 ]] || { echo 'RC22_STOP=POSTGRES_NOT_READY'; exit 3; }
echo 'RC22_POSTGRES_READY=YES'
work="$(mktemp -d /tmp/rc22-schema.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
versions="001_ticket_workflow 003_telegram_intake 004_operator_proposals 005_encrypted_telegram_reviews 006_telegram_poll_cursor"
for v in $versions; do
 git --git-dir="$repo" show "$sha:db/migrations/$v.sql" > "$work/$v.sql" || {
  echo 'RC22_STOP=PINNED_MIGRATION_NOT_FOUND'; exit 3;
 }
done
if [[ "$created" == 1 ]]; then
 {
  echo 'BEGIN;'
  for v in $versions; do cat "$work/$v.sql"; printf '\n'; done
  cat <<'SQL'
CREATE TABLE residentchat.schema_bootstrap (
  version integer PRIMARY KEY CHECK (version=6),
  installed_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO residentchat.schema_bootstrap(version) VALUES (6);
COMMIT;
SQL
 } | pg >/dev/null || { echo 'RC22_STOP=MIGRATIONS_FAILED_DATA_UNTOUCHED_BY_CLEANUP'; exit 3; }

 # Only a synthetic lab runtime can connect; never use postgres for app.
 # Hex credentials transmitted in psql STDIN, not command argv or Git.
 {
  printf "\\set runtime_pw '%s'\n" "$(< "$runtime")"
  cat <<'SQL'
SELECT format('CREATE ROLE residentchat_lab_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD %L', :'runtime_pw')
WHERE NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname = 'residentchat_lab_runtime')
\gexec
GRANT CONNECT ON DATABASE residentchat TO residentchat_lab_runtime;
GRANT USAGE ON SCHEMA residentchat TO residentchat_lab_runtime;
GRANT SELECT, INSERT, UPDATE ON residentchat.tickets TO residentchat_lab_runtime;
GRANT SELECT, INSERT ON residentchat.ticket_audit TO residentchat_lab_runtime;
GRANT SELECT, INSERT ON residentchat.telegram_intake TO residentchat_lab_runtime;
GRANT SELECT, INSERT, UPDATE ON residentchat.operator_proposals TO residentchat_lab_runtime;
GRANT SELECT, INSERT ON residentchat.operator_proposal_decisions TO residentchat_lab_runtime;
GRANT SELECT, INSERT ON residentchat.telegram_reviews TO residentchat_lab_runtime;
GRANT INSERT ON residentchat.telegram_review_reads TO residentchat_lab_runtime;
GRANT SELECT, INSERT, UPDATE ON residentchat.telegram_poll_cursors TO residentchat_lab_runtime;
GRANT SELECT, INSERT ON residentchat.telegram_poll_blockers TO residentchat_lab_runtime;
GRANT USAGE, SELECT ON SEQUENCE residentchat.ticket_audit_id_seq TO residentchat_lab_runtime;
SQL
 } | pg >/dev/null || { echo 'RC22_STOP=LAB_ROLE_CREATION_FAILED'; exit 3; }
 echo 'RC22_MIGRATIONS_APPLIED_ONCE=YES'
else
 echo 'RC22_MIGRATIONS_NOT_REAPPLIED=YES'
fi
version="$(printf 'SELECT version FROM residentchat.schema_bootstrap;\n' | pg -t -A | tr -d '[:space:]')" || {
 echo 'RC22_STOP=SCHEMA_LEDGER_MISSING'; exit 3;
}
[[ "$version" == 6 ]] || { echo 'RC22_STOP=SCHEMA_VERSION_NOT_EXPECTED'; exit 3; }
safe_role="$(printf "SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls FROM pg_roles WHERE rolname='residentchat_lab_runtime';\n" | pg -t -A | tr -d '[:space:]')" || {
 echo 'RC22_STOP=RUNTIME_ROLE_MISSING'; exit 3;
}
[[ "$safe_role" == t ]] || { echo 'RC22_STOP=RUNTIME_ROLE_UNSAFE'; exit 3; }
echo 'RC22_DATABASE=residentchat'
echo 'RC22_SCHEMA_VERSION=6'
echo 'RC22_PERSISTENT_DATA_VOLUME=residentchat-pg17-data'
echo 'RC22_PERSISTENT_SOCKET_VOLUME=residentchat-pg17-socket'
echo 'RC22_NETWORK=NONE'
echo 'RC22_RUNTIME_ROLE_RLS_SAFE=YES'
echo 'RC22_TELEGRAM_POLLING=OFF'
echo 'RC22_TELEGRAM_UPDATES_ACKED=NO'
echo 'RC22_PERSISTENT_POSTGRES_PASS'
