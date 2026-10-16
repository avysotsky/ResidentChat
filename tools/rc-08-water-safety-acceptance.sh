#!/usr/bin/env bash
# RC-08: strict one-shot OFFLINE C# smoke/regression gate.
# NO real Telegram, resident messages, PostgreSQL, Ollama, or network.
set -Eeuo pipefail
umask 077

if [[ $# -ne 1 || ! "$1" =~ ^[0-9a-f]{40}$ || "$(id -u)" == 0 ]]; then
    echo 'RC-08 NOT_RUN: provide an exact 40-character commit SHA as unprivileged WSL user'
    exit 2
fi
sha="$1"
mirror=/home/user/ResidentChatPRAutotest/source.git
cache=/home/user/ResidentChatPRAutotest/nuget
lock=/home/user/WSLAutotest/execution.lock
image=jobradar-pr-autotest:dotnet10

for binary in git docker tar flock timeout mktemp; do
    command -v "$binary" >/dev/null || {
        echo "RC-08 NOT_RUN: missing host utility $binary"
        exit 2
    }
done

[[ -d "$mirror" && -f "$lock" ]] || {
    echo 'RC-08 NOT_RUN: owner WSL2 runner mirror or lock not found'
    exit 2
}
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC-08 NOT_RUN: owner runner is busy'; exit 2; }
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo 'RC-08 NOT_RUN: exact source commit absent from git mirror'
    exit 2
}
docker image inspect "$image" >/dev/null || {
    echo 'RC-08 NOT_RUN: offline .NET 10 image not installed'
    exit 2
}
if [[ -d "$cache/packages/npgsql" ]]; then
    packages=/nuget/packages
elif [[ -d "$cache/npgsql" ]]; then
    packages=/nuget
else
    echo 'RC-08 NOT_RUN: preinstalled offline NuGet cache is missing'
    exit 2
fi

work="$(mktemp -d /tmp/rc08-water-safety.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -m 700 "$work/src"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/src"
[[ -f "$work/src/tests/ResidentChat.SmokeTests/Program.cs" ]] || {
    echo 'RC-08 NOT_RUN: test source absent from exact SHA'
    exit 2
}
echo "RC-08 pinned source: $sha"
echo 'RC-08 running synthetic C# triage and non-escalation cases in networkless .NET 10...'
timeout 240 docker run --rm --pull never \
    --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
    --pids-limit 128 --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$work/src,dst=/work" \
    --mount "type=bind,src=$cache,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$packages" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e DOTNET_NOLOGO=1 \
    "$image" /bin/bash -c '
    set -Eeuo pipefail
    dotnet restore tests/ResidentChat.SmokeTests/ResidentChat.SmokeTests.csproj \
        --source "$NUGET_PACKAGES" --ignore-failed-sources --verbosity quiet
    dotnet run -c Release --no-restore --no-launch-profile \
        --project tests/ResidentChat.SmokeTests/ResidentChat.SmokeTests.csproj
    '

echo 'RC-08 OFFLINE WATER SAFETY PASS (synthetic fake-model cases only)'
echo 'RC-08 cleanup: disposable source workspace removed by exit handler'
