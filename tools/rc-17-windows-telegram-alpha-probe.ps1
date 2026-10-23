# RC-17: read exact synthetic Telegram marker, dispatch numeric ID only.
# No plaintext token outside this DPAPI-bound Windows process.
# No getUpdates offset, no ACK and no Telegram writes.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedUser = 'residentchat_od_lab_bot'
$alpha = [long]-5512397750
$probe = 'RC_ALPHA_PROBE_20261023'
$sha = '85f81bae4037667e5b0947d3a0168ba9ee79a51e'
$folder = Join-Path (Join-Path $env:LOCALAPPDATA 'ResidentChat') 'Secrets'
$tokenFile = Join-Path $folder 'telegram-lab-token.dpapi'
$alphaFile = Join-Path $folder 'alpha-chat-id.txt'
$pointer = [IntPtr]::Zero
$token = $null
$secure = $null
try {
    if ($env:OS -ne 'Windows_NT' -or
        -not (Test-Path -LiteralPath $tokenFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $alphaFile -PathType Leaf)) {
        throw 'RC17_VAULT_UNAVAILABLE'
    }
    $savedId = [long]::Parse(([System.IO.File]::ReadAllText($alphaFile)).Trim(),
        [System.Globalization.CultureInfo]::InvariantCulture)
    if ($savedId -ne $alpha) {
        throw 'RC17_LOCAL_ALPHA_ID_MISMATCH'
    }
    $secure = ConvertTo-SecureString -String ([System.IO.File]::ReadAllText($tokenFile))
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($token -notmatch '^[0-9]{6,16}:[A-Za-z0-9_-]{20,}$') {
        throw 'RC17_BOT_TOKEN_INVALID'
    }
    # Token is in Telegram HTTPS URI, but no exception details are printed.
    $base = 'https://api.telegram.org/bot' + $token
    $me = Invoke-RestMethod -Method Post -Uri ($base + '/getMe') -TimeoutSec 15
    $hook = Invoke-RestMethod -Method Post -Uri ($base + '/getWebhookInfo') -TimeoutSec 15
    if (-not $me.ok -or -not $me.result.is_bot -or
        $me.result.username -cne $expectedUser -or
        $me.result.can_read_all_group_messages -ne $true -or
        -not $hook.ok -or -not [string]::IsNullOrEmpty($hook.result.url)) {
        throw 'RC17_TELEGRAM_PREFLIGHT_INVALID'
    }
    Write-Host 'RC17_BOT_IDENTITY_AND_PRIVACY_PASS' -ForegroundColor Green
    Write-Host 'RC17_WEBHOOK=NONE'

    # No offset. Other updates remain completely local and are discarded.
    $resp = Invoke-RestMethod -Method Post -Uri ($base + '/getUpdates') -ContentType 'application/json' -Body '{"limit":100,"timeout":0}' -TimeoutSec 20
    if (-not $resp.ok) {
        throw 'RC17_TELEGRAM_UPDATES_UNAVAILABLE'
    }
    # Updates may be service events without a message or text property.
    # Under PowerShell StrictMode, accessing a missing property throws.
    # Inspect only existing fields, never serialize or log unrelated updates.
    $candidate = @(
        foreach ($update in @($resp.result)) {
            if ($null -eq $update) { continue }
            $msgField = $update.PSObject.Properties['message']
            $idField = $update.PSObject.Properties['update_id']
            if ($null -eq $msgField -or $null -eq $idField -or
                $null -eq $msgField.Value) { continue }
            $msg = $msgField.Value
            $textField = $msg.PSObject.Properties['text']
            $chatField = $msg.PSObject.Properties['chat']
            if ($null -eq $textField -or $null -eq $chatField -or
                $null -eq $chatField.Value) { continue }
            $chat = $chatField.Value
            $chatIdField = $chat.PSObject.Properties['id']
            $chatTypeField = $chat.PSObject.Properties['type']
            if ($null -eq $chatIdField -or $null -eq $chatTypeField) {
                continue
            }
            $number = [long]0
            if ($textField.Value -cne $probe -or
                -not [long]::TryParse([string]$chatIdField.Value, [ref]$number) -or
                $number -ne $alpha -or $chatTypeField.Value -ne 'group') {
                continue
            }
            if (-not [long]::TryParse([string]$idField.Value, [ref]$number) -or
                $number -le 0) { continue }
            [pscustomobject]@{ UpdateId = $number }
        }
    )
    $candidate = @($candidate | Sort-Object -Property UpdateId -Descending)
    if ($candidate.Count -eq 0) {
        Write-Host 'RC17_PROBE_NOT_FOUND' -ForegroundColor Yellow
        Write-Host 'RC17_UPDATES_ACKED=NO'
        Write-Host 'RC17_ACTION=POST_EXACT_TEST_MARKER_AGAIN'
        return
    }
    $updateId = [long]$candidate[0].UpdateId
    Write-Host 'RC17_ALPHA_EXACT_SYNTHETIC_PROBE_FOUND=YES' -ForegroundColor Green
    Write-Host 'RC17_RAW_TELEGRAM_MESSAGES_FORWARDED=NO'
    Write-Host 'RC17_UPDATES_ACKED=NO'
    Write-Host 'RC17_WSL_INPUT=NUMERIC_UPDATE_ID_ONLY'

    # WSL receives only the numeric update ID as a positional argument.
    # Windows PowerShell 5.1/native WSL quoting may strip the quote marks
    # in a Bash -c script that uses "$1" / "${sha}". That expanded the
    # prior Git object spec into a bare ':tools/...' path. Interpolate
    # STRICTLY VALIDATED SHA and numeric ID instead: no nested quoting,
    # no shell-special characters, no token and no raw Telegram update.
    if ($sha -cnotmatch '^[0-9a-f]{40}
    if ($LASTEXITCODE -ne 0) {
        throw 'RC17_LAB_GATE_FAILED'
    }
    Write-Host 'RC17_TELEGRAM_ALPHA_SANITIZED_BRIDGE_PASS' -ForegroundColor Green
}
catch {
    Write-Host 'RC17_ONE_SHOT_GATE_FAILED' -ForegroundColor Red
    throw 'RC17_FAILED_SAFE'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    Remove-Variable token, secure, base, me, hook, resp, candidate, updateId, runner -ErrorAction SilentlyContinue
}
 -or
        ([string]$updateId) -notmatch '^[1-9][0-9]{0,17}
    if ($LASTEXITCODE -ne 0) {
        throw 'RC17_LAB_GATE_FAILED'
    }
    Write-Host 'RC17_TELEGRAM_ALPHA_SANITIZED_BRIDGE_PASS' -ForegroundColor Green
}
catch {
    Write-Host 'RC17_ONE_SHOT_GATE_FAILED' -ForegroundColor Red
    throw 'RC17_FAILED_SAFE'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    Remove-Variable token, secure, base, me, hook, resp, candidate, updateId, runner -ErrorAction SilentlyContinue
}
) {
        throw 'RC17_INVALID_PIN_OR_UPDATE_ID'
    }
    $runner = 'set -Eeuo pipefail; git --git-dir=/home/user/ResidentChatPRAutotest/source.git show {0}:tools/rc-17-alpha-probe-gate.sh | bash -s -- {0} {1}' -f $sha, ([string]$updateId)
    & wsl.exe -u user -- bash -c $runner
    if ($LASTEXITCODE -ne 0) {
        throw 'RC17_LAB_GATE_FAILED'
    }
    Write-Host 'RC17_TELEGRAM_ALPHA_SANITIZED_BRIDGE_PASS' -ForegroundColor Green
}
catch {
    Write-Host 'RC17_ONE_SHOT_GATE_FAILED' -ForegroundColor Red
    throw 'RC17_FAILED_SAFE'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    Remove-Variable token, secure, base, me, hook, resp, candidate, updateId, runner -ErrorAction SilentlyContinue
}
