#!/usr/bin/env bash
# RC-12 Windows-local Ollama from pinned WSL mirror, under shared WSL lock.
set -Eeuo pipefail
umask 077
sha="${1:-}"
mode="${2:-}"
if [[ $# != 2 || ! "$sha" =~ ^[0-9a-f]{40}$ ||
    ( "$mode" != "--probe" && "$mode" != "--run" ) || "$(id -u)" == 0 ]]; then
  echo 'RC-12 NOT_RUN: provide pinned SHA --probe|--run as non-root user'; exit 2
fi
mirror=/home/user/ResidentChatPRAutotest/source.git
lock=/home/user/WSLAutotest/execution.lock
powershell=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
tempdir=/mnt/c/Users/User/AppData/Local/Temp
for cmd in git flock mktemp wslpath; do command -v "$cmd" >/dev/null || exit 2; done
[[ -d "$mirror" && -f "$lock" && -f "$powershell" && -d "$tempdir" ]] || {
  echo 'RC-12 NOT_RUN: WSL mirror, lock or Windows PowerShell unavailable'; exit 2
}
git --git-dir="$mirror" cat-file -e "$sha:tools/rc-12-windows-qwen-eval.ps1" || {
  echo 'RC-12 NOT_RUN: pinned source not yet mirrored by WSL watcher'; exit 2
}
echo 'RC-12 waiting for shared WSL test lock (up to 120s)'
exec 9<>"$lock"
flock -w 120 9 || { echo 'RC-12 NOT_RUN: shared lock busy'; exit 2; }
work="$(mktemp -d "$tempdir/rc12-win.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
# Windows PowerShell 5.1 needs a BOM for Cyrillic UTF-8 source.
printf '\357\273\277' > "$work/eval.ps1"
git --git-dir="$mirror" show "$sha:tools/rc-12-windows-qwen-eval.ps1" >> "$work/eval.ps1"
path="$(wslpath -w "$work/eval.ps1")"
echo "RC-12 Windows localhost mode=$mode"
if [[ "$mode" == "--probe" ]]; then
 "$powershell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$path" -Mode probe
else
 echo 'RC-12 explicitly loading already-installed model for synthetic inference'
 "$powershell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$path" -Mode run
fi
