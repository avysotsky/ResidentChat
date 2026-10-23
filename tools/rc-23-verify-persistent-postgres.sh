#!/usr/bin/env bash
# RC-23: verify the SAME ResidentChat PostgreSQL survives a controlled
# container restart. Never create/drop databases, volume or any user row.
# No Telegram/Ollama/API and no external connections.
set -Eeuo pipefail
umask 077
sha="$1"
if [[ $# != 1 || ! "$sha" =~ ^[a-f0-9]{40}$ || "$(id -u)" == 0 ]]; then
  echo 'RC23_NOT_RUN=INVALID_SHA_OR_ROOT'; exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
lock=/home/user/WSLAutotest/execution.lock
name=residentchat-pg17
volume=residentchat-pg17-data
socket=residentchat-pg17-socket
image=postgres:17-bookworm
for utility in git docker flock grep; do
  command -v "$utility" >/dev/null || {
    echo 'RC23_NOT_RUN=HOST_DEPENDENCY_MISSING'; exit 2
  }
done
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC23_NOT_RUN=SHARED_LOCK_BUSY'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" 2>/dev/null || {
  echo 'RC23_NOT_RUN=PINNED_COMMIT_NOT_FETCHED'; exit 2
}
docker container inspect "$name" >/dev/null 2>&1 || {
  echo 'RC23_STOP=PERSISTENT_CONTAINER_NOT_FOUND'; exit 3
}
docker volume inspect "$volume" >/dev/null 2>&1 &&
docker volume inspect "$socket" >/dev/null 2>&1 || {
  echo 'RC23_STOP=PERSISTENT_VOLUME_NOT_FOUND'; exit 3
}
[[ "$(docker inspect -f '{{.Config.Image}}' "$name")" == "$image" &&
   "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$name")" == none &&
   "$(docker inspect -f '{{.State.Running}}' "$name")" == true ]] || {
  echo 'RC23_STOP=CONTAINER_IDENTITY_OR_STATE_UNEXPECTED'; exit 3
}
mounts="$(docker inspect -f '{{range .Mounts}}{{println .Name .Destination}}{{end}}' "$name")"
grep -Fqx "$volume /var/lib/postgresql/data" <<<"$mounts" &&
grep -Fqx "$socket /var/run/postgresql" <<<"$mounts" || {
  echo 'RC23_STOP=VOLUME_MOUNT_MISMATCH'; exit 3
}
# Any PostgreSQL detail/error could be confidential; never print raw SQL
# or exception text. Password remains inside container-mounted secret file.
pg() {
  docker exec -i --user 0 "$name" sh -c \
    'PGPASSWORD="$(cat /run/secrets/pg_admin)"; export PGPASSWORD; exec psql -X -q -t -A -v ON_ERROR_STOP=1 -U postgres -d residentchat' 2>/dev/null
}
query="SELECT md5((pg_control_system()).system_identifier::text || ':' ||
  manifest_sha256 || ':' || installed_at::text)
  FROM residentchat.schema_bootstrap WHERE version = 6;"
health="SELECT
   (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname = 'residentchat') >= 10
   AND (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='residentchat' AND c.relkind='r'
          AND c.relname IN ('tickets','ticket_audit','telegram_intake',
          'operator_proposals','operator_proposal_decisions','telegram_reviews',
          'telegram_review_reads','telegram_poll_cursors','telegram_poll_blockers')
          AND c.relrowsecurity AND c.relforcerowsecurity) = 9
   AND (SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
        FROM pg_roles WHERE rolname = 'residentchat_lab_runtime')
   AND current_setting('server_version_num')::int >= 170000;"
before="$(printf '%s\n' "$query" | pg | tr -d '[:space:]')" || {
  echo 'RC23_STOP=BEFORE_DATABASE_READ_FAILED'; exit 3
}
[[ "$before" =~ ^[a-f0-9]{32}$ ]] || {
  echo 'RC23_STOP=BEFORE_CLUSTER_FINGERPRINT_MISSING'; exit 3
}
before_health="$(printf '%s\n' "$health" | pg | tr -d '[:space:]')" || {
  echo 'RC23_STOP=BEFORE_HEALTH_CHECK_FAILED'; exit 3
}
[[ "$before_health" == t ]] || {
  echo 'RC23_STOP=RLS_SCHEMA_ROLE_OR_VERSION_INVALID'; exit 3
}
# Exercise one real read as the unprivileged DB runtime identity.
# Even with an accidentally missed tenant filter, RLS returns zero.
rls_result="$(printf 'BEGIN; SET LOCAL ROLE residentchat_lab_runtime; SELECT count(*) FROM residentchat.telegram_poll_cursors; ROLLBACK;\n' | pg | tr -d '[:space:]')" || {
  echo 'RC23_STOP=RUNTIME_RLS_TEST_FAILED'; exit 3
}
[[ "$rls_result" == 0 ]] || {
  echo 'RC23_STOP=RUNTIME_DEFAULT_DENY_FAILED'; exit 3
}
echo 'RC23_PRE_RESTART_SCHEMA_RLS_PASS'
echo 'RC23_RESTARTING_ONLY_RESIDENTCHAT_CONTAINER=YES'
docker restart --time 20 "$name" >/dev/null 2>&1 || {
  echo 'RC23_STOP=CONTAINER_RESTART_FAILED'; exit 3
}
ready=0
for ((i=0;i<90;i++)); do
  if docker exec "$name" pg_isready -q -h /var/run/postgresql -U postgres -d residentchat >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 1
done
[[ "$ready" == 1 ]] || { echo 'RC23_STOP=RESTART_NOT_READY'; exit 3; }
after="$(printf '%s\n' "$query" | pg | tr -d '[:space:]')" || {
  echo 'RC23_STOP=AFTER_DATABASE_READ_FAILED'; exit 3
}
after_health="$(printf '%s\n' "$health" | pg | tr -d '[:space:]')" || {
  echo 'RC23_STOP=AFTER_HEALTH_CHECK_FAILED'; exit 3
}
[[ "$before" == "$after" && "$after_health" == t ]] || {
  echo 'RC23_STOP=PERSISTENCE_OR_SCHEMA_DRIFT'; exit 3
}
echo 'RC23_CLUSTER_IDENTITY_PRESERVED=YES'
echo 'RC23_MIGRATION_LEDGER_PRESERVED=YES'
echo 'RC23_RLS_AND_RUNTIME_ROLE_PRESERVED=YES'
echo 'RC23_DOCKER_VOLUME_PRESERVED=YES'
echo 'RC23_DATABASE=residentchat'
echo 'RC23_TELEGRAM_POLLING=OFF'
echo 'RC23_UPDATES_ACKED=NO'
echo 'RC23_PERSISTENCE_RESTART_PASS'
