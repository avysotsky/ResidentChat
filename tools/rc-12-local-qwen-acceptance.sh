#!/usr/bin/env bash
# RC-12: exact-SHA, bounded local-Qwen classification acceptance (opt-in).
# Existing, preloaded Ollama ONLY. Never starts/downloads/unloads models.
# No Telegram, PostgreSQL, resident text, Git writes or production actions.
set -Eeuo pipefail
umask 077
sha="${1:-}"
if [[ $# != 1 || ! "$sha" =~ ^[a-f0-9]{40}$ || "$(id -u)" == 0 ]]; then
    echo 'RC-12 NOT_RUN: exact SHA required as non-root WSL user'
    exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
nuget=/home/user/ResidentChatPRAutotest/nuget
lock=/home/user/WSLAutotest/execution.lock
sdk=jobradar-pr-autotest:dotnet10
for cmd in git curl docker tar flock timeout mktemp node; do
    command -v "$cmd" >/dev/null || { echo "RC-12 NOT_RUN: missing $cmd"; exit 2; }
done
[[ -d "$mirror" && -f "$lock" ]] || {
    echo 'RC-12 NOT_RUN: private WSL runner mirror or lock absent'
    exit 2
}
git --git-dir="$mirror" cat-file -e "$sha^{commit}" || {
    echo 'RC-12 NOT_RUN: exact SHA not synced to WSL source mirror'
    exit 2
}
docker image inspect "$sdk" >/dev/null 2>&1 || {
    echo 'RC-12 NOT_RUN: .NET 10 SDK image missing'
    exit 2
}
if [[ -d "$nuget/packages/npgsql" ]]; then
    packages=/nuget/packages
elif [[ -d "$nuget/npgsql" ]]; then
    packages=/nuget
else
    echo 'RC-12 NOT_RUN: offline NuGet cache missing'
    exit 2
fi
echo 'RC-12 waiting for existing shared WSL test lock (up to 120 seconds)'
exec 9<>"$lock"
flock -w 120 9 || {
    echo 'RC-12 NOT_RUN: shared WSL/TradeOps testing lock occupied'
    exit 2
}
# Probe only the loopback interface visible to WSL. Never scan gateways,
# bind Windows services, start Ollama, auto-pull or activate a cold model.
base=http://127.0.0.1:11434
if ! curl --noproxy '*' --fail --silent --max-time 3 "$base/api/version" \
    -o /dev/null; then
    echo 'RC-12 NOT_RUN: WSL2 cannot reach EXISTING local Ollama on 127.0.0.1:11434'
    echo 'RC-12 NOTE: Windows Ollama and WSL2 localhost may be isolated by network mode'
    exit 2
fi
if ! curl --noproxy '*' --fail --silent --max-time 3 "$base/api/ps" |
    node -e '
      let b = "";
      process.stdin.setEncoding("utf8");
      process.stdin.on("data", c => { b += c; if (b.length > 8192) process.exit(2); });
      process.stdin.on("end", () => {
        try {
          const models = JSON.parse(b).models;
          process.exit(Array.isArray(models) && models.some(m =>
            m && (m.name === "qwen3:1.7b" || m.model === "qwen3:1.7b")) ? 0 : 2);
        } catch { process.exit(2); }
      });
    '; then
    echo 'RC-12 NOT_RUN: qwen3:1.7b is not ALREADY loaded in the local Ollama'
    echo 'RC-12 NOTE: gate will not load models or disturb other PC workloads'
    exit 2
fi

work="$(mktemp -d /tmp/rc12-qwen.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -m 700 "$work/src"
git --git-dir="$mirror" archive "$sha" | tar -xf - -C "$work/src"
test -f "$work/src/tools/ResidentChat.ModelEvaluation/Program.cs" || {
    echo 'RC-12 NOT_RUN: model evaluator source absent at pinned SHA'
    exit 2
}
echo "RC-12 starting one bounded, sequential classifier run sha=$sha"
echo 'RC-12 WARNING: uses the existing local model; run only when concurrent work is idle'
# .NET SDK uses host networking ONLY to access local Ollama loopback.
# No external model endpoint is accepted by LocalModelClassifier.
# OS firewall isolation is not asserted by this synthetic lab.
timeout 150 docker run --rm --pull never --network host --read-only \
    --cpus 1 --memory 768m --memory-swap 768m --pids-limit 96 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --user "$(id -u):$(id -g)" --workdir /work \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --mount "type=bind,src=$work/src,dst=/work" \
    --mount "type=bind,src=$nuget,dst=/nuget,readonly" \
    -e "NUGET_PACKAGES=$packages" \
    -e DOTNET_CLI_HOME=/tmp/dotnet-home \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    -e DOTNET_NOLOGO=1 \
    "$sdk" /bin/bash -c '
      set -Eeuo pipefail
      dotnet restore tools/ResidentChat.ModelEvaluation/ResidentChat.ModelEvaluation.csproj \
          --source "$NUGET_PACKAGES" --ignore-failed-sources -v quiet
      dotnet run -c Release --no-restore --no-launch-profile \
          --project tools/ResidentChat.ModelEvaluation/ResidentChat.ModelEvaluation.csproj \
          -- --synthetic-loaded-model
    '
echo 'RC-12 BOUNDED LIVE LOCAL CLASSIFIER PASS'
