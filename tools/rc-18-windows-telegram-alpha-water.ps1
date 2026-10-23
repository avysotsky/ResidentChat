# RC-18: read *actual* synthetic water text from Telegram; dispatch only
# exact allowlisted text and numeric ID to disposable local WSL acceptance.
# No plaintext token outside this DPAPI-bound Windows process; synthetic text
# crosses to WSL only after exact equality with a preapproved fixture.
# No getUpdates offset, no Telegram ACK, no bot writes or background service.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedUser = 'residentchat_od_lab_bot'
$alpha = [long]-5512397750
# Explicit ASCII-only code points prevent PowerShell 5.1 console encoding issues.
$probe = -join ([char[]]@(0x423, 0x20, 0x43f, 0x456, 0x434, 0x432, 0x430, 0x43b, 0x456, 0x20, 0x442, 0x435, 0x447, 0x435, 0x20, 0x432, 0x43e, 0x434, 0x430, 0x20, 0x437, 0x20, 0x442, 0x440, 0x443, 0x431, 0x438))
$sha = '08a25533c63637b8994335a987f6a758391a3ca5'
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
        throw 'RC18_VAULT_UNAVAILABLE'
    }
    $savedId = [long]::Parse(([System.IO.File]::ReadAllText($alphaFile)).Trim(),
        [System.Globalization.CultureInfo]::InvariantCulture)
    if ($savedId -ne $alpha) {
        throw 'RC18_LOCAL_ALPHA_ID_MISMATCH'
    }
    $secure = ConvertTo-SecureString -String ([System.IO.File]::ReadAllText($tokenFile))
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($token -notmatch '^[0-9]{6,16}:[A-Za-z0-9_-]{20,}$') {
        throw 'RC18_BOT_TOKEN_INVALID'
    }
    # Token is in Telegram HTTPS URI, but no exception details are printed.
    $base = 'https://api.telegram.org/bot' + $token
    $me = Invoke-RestMethod -Method Post -Uri ($base + '/getMe') -TimeoutSec 15
    $hook = Invoke-RestMethod -Method Post -Uri ($base + '/getWebhookInfo') -TimeoutSec 15
    if (-not $me.ok -or -not $me.result.is_bot -or
        $me.result.username -cne $expectedUser -or
        $me.result.can_read_all_group_messages -ne $true -or
        -not $hook.ok -or -not [string]::IsNullOrEmpty($hook.result.url)) {
        throw 'RC18_TELEGRAM_PREFLIGHT_INVALID'
    }
    Write-Host 'RC18_BOT_IDENTITY_AND_PRIVACY_PASS' -ForegroundColor Green
    Write-Host 'RC18_WEBHOOK=NONE'

    # No offset. Other updates remain completely local and are discarded.
    $resp = Invoke-RestMethod -Method Post -Uri ($base + '/getUpdates') -ContentType 'application/json' -Body '{"limit":100,"timeout":0}' -TimeoutSec 20
    if (-not $resp.ok) {
        throw 'RC18_TELEGRAM_UPDATES_UNAVAILABLE'
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
            [pscustomobject]@{ UpdateId = $number; Text = [string]$textField.Value }
        }
    )
    $candidate = @($candidate | Sort-Object -Property UpdateId -Descending)
    if ($candidate.Count -eq 0) {
        Write-Host 'RC18_PROBE_NOT_FOUND' -ForegroundColor Yellow
        Write-Host 'RC18_UPDATES_ACKED=NO'
        Write-Host 'RC18_ACTION=POST_EXACT_TEST_MARKER_AGAIN'
        return
    }
    $updateId = [long]$candidate[0].UpdateId
    # Encode the bytes from Telegram message.text (not a reconstructed copy).
    # The exact equality filter above rejects all other message content.
    $textB64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes([string]$candidate[0].Text))
    Write-Host 'RC18_ALPHA_EXACT_SYNTHETIC_PROBE_FOUND=YES' -ForegroundColor Green
    Write-Host 'RC18_ARBITRARY_TELEGRAM_MESSAGES_FORWARDED=NO'
    Write-Host 'RC18_EXACT_ALLOWLISTED_TEXT_TRANSPORT=YES'
    Write-Host 'RC18_UPDATES_ACKED=NO'
    Write-Host 'RC18_WSL_INPUT=NUMERIC_ID_AND_ALLOWLISTED_TEXT_ONLY'

    # WSL receives only the numeric update ID as a positional argument.
    # Windows PowerShell 5.1/native WSL quoting can strip the quotes
    # around nested Bash positional variables. Embed only validated
    # hexadecimal SHA and numeric ID; do not pass tokens or messages.
    if ($sha -cnotmatch '^[0-9a-f]{40}$' -or
        ([string]$updateId) -notmatch '^[1-9][0-9]{0,17}$' -or
        $textB64 -cnotmatch '^[A-Za-z0-9+/=]{8,1024}$') {
        throw 'RC18_INVALID_PIN_OR_UPDATE_ID'
    }
    $runner = 'set -Eeuo pipefail; git --git-dir=/home/user/ResidentChatPRAutotest/source.git show {0}:tools/rc-18-alpha-water-realtext-gate.sh | bash -s -- {0} {1} {2}' -f $sha, ([string]$updateId), $textB64
    & wsl.exe -u user -- bash -c $runner
    if ($LASTEXITCODE -ne 0) {
        throw 'RC18_LAB_GATE_FAILED'
    }
    Write-Host 'RC18_TELEGRAM_ALPHA_REAL_TEXT_BRIDGE_PASS' -ForegroundColor Green
}
catch {
    Write-Host 'RC18_ONE_SHOT_GATE_FAILED' -ForegroundColor Red
    throw 'RC18_FAILED_SAFE'
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    Remove-Variable token, secure, base, me, hook, resp, candidate, updateId, runner, textB64 -ErrorAction SilentlyContinue
}
