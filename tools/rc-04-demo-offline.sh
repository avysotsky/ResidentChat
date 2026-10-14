#!/usr/bin/env bash
# Local, one-shot, synthetic-only ResidentChat operator-demo launcher.
# No Telegram, Ollama, production database, network or installed system services.
set -Eeuo pipefail
umask 077

sha="$1"
if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]] || [[ "$(id -u)" == 0 ]]; then
    echo "RC-04 NOT_RUN: specify pinned Git SHA and unprivileged WSL user"
    exit 2
fi

mirror="/home/user/ResidentChatPRAutotest/source.git"
cache="/home/user/ResidentChatPRAutotest/nuget"
lock="/home/user/WSLAutotest/execution.lock"
image="jobradar-pr-autotest:dotnet10"

for binary in git docker tar flock timeout mktemp; do
    command -v "$binary" >/dev/null || {
        echo "RC-04 NOT_RUN: required utility missing"
        exit 2
    }
done

exec 9<> "$lock"
if ! flock -w 120 9; then
    echo "RC-04 NOT_RUN: WSL runner lock busy"
    exit 2
fi

git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo "RC-04 NOT_RUN: commit is not in local Git cache"
    exit 2
}
docker image inspect "$image" >/dev/null || {
    echo "RC-04 NOT_RUN: .NET 10 image missing"
    exit 2
}

if [[ -d "$cache/packages/npgsql" ]]; then
    nuget_mount="/nuget/packages"
elif [[ -d "$cache/npgsql" ]]; then
    nuget_mount="/nuget"
else
    echo "RC-04 NOT_RUN: offline NuGet cache unavailable"
    exit 2
fi

work="$(mktemp -d /tmp/residentchat-demo.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -m 700 "$work/source"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/source"
if [[ ! -f "$work/source/tools/ResidentChat.Demo/Program.cs" ]]; then
    echo "RC-04 NOT_RUN: demo executable missing from commit"
    exit 2
fi

echo "RC-04 synthetic demo pinned commit: $sha"
timeout 180 docker run --rm --pull never \
    --network none --read-only --cpus 2 --memory 1g --memory-swap 1g \
    --pids-limit 128 --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$work/source,dst=/work" \
    --mount "type=bind,src=$cache,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$nuget_mount" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e DOTNET_NOLOGO=1 \
    "$image" /bin/bash -c \
    'set -Eeuo pipefail
     dotnet restore tools/ResidentChat.Demo/ResidentChat.Demo.csproj --source "$NUGET_PACKAGES" --ignore-failed-sources --verbosity quiet
     dotnet run -c Release --no-restore --project tools/ResidentChat.Demo/ResidentChat.Demo.csproj -- --offline'

echo "RC-04 OFFLINE DEMO PASS (scripted fixtures, NOT a Qwen inference test)"
